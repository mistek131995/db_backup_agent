using System.Diagnostics;
using System.Text;
using BackupsterAgent.Configuration;
using BackupsterAgent.Exceptions;
using BackupsterAgent.Services.Common.Resolvers;
using MySqlConnector;

namespace BackupsterAgent.Providers.Restore;

public sealed class MysqlRestoreProvider : IRestoreProvider
{
    private readonly ILogger<MysqlRestoreProvider> _logger;
    private readonly MysqlBinaryResolver _binaryResolver;

    public MysqlRestoreProvider(
        ILogger<MysqlRestoreProvider> logger,
        MysqlBinaryResolver binaryResolver)
    {
        _logger = logger;
        _binaryResolver = binaryResolver;
    }

    public async Task ValidatePermissionsAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct)
    {
        const string globalSql = @"
SELECT
  MAX(CASE WHEN PRIVILEGE_TYPE IN ('CREATE', 'ALL PRIVILEGES') THEN 1 ELSE 0 END) AS has_create,
  MAX(CASE WHEN PRIVILEGE_TYPE IN ('DROP',   'ALL PRIVILEGES') THEN 1 ELSE 0 END) AS has_drop
FROM information_schema.USER_PRIVILEGES;";

        const string schemaSql = @"
SELECT
  MAX(CASE WHEN PRIVILEGE_TYPE IN ('CREATE', 'ALL PRIVILEGES') THEN 1 ELSE 0 END) AS has_create,
  MAX(CASE WHEN PRIVILEGE_TYPE IN ('DROP',   'ALL PRIVILEGES') THEN 1 ELSE 0 END) AS has_drop
FROM information_schema.SCHEMA_PRIVILEGES
WHERE TABLE_SCHEMA = @db;";

        await using var conn = new MySqlConnection(BuildAdminConnectionString(connection));
        await conn.OpenAsync(ct);

        var (globalCreate, globalDrop) = await ReadPrivilegePairAsync(conn, globalSql, null, ct);
        if (globalCreate && globalDrop) return;

        var (schemaCreate, schemaDrop) = await ReadPrivilegePairAsync(conn, schemaSql, targetDatabase, ct);

        if ((globalCreate || schemaCreate) && (globalDrop || schemaDrop))
            return;

        throw new RestorePermissionException(
            $"Пользователь '{connection.Username}' подключения '{connection.Name}' не имеет прав для восстановления БД '{targetDatabase}'. " +
            "Требуются привилегии CREATE и DROP глобально (ON *.*) либо на целевую БД. " +
            $"Выдайте права: GRANT CREATE, DROP ON *.* TO '{connection.Username}'@'%'; FLUSH PRIVILEGES;.");
    }

    public async Task PrepareTargetDatabaseAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct)
    {
        var quoted = QuoteIdentifier(targetDatabase);

        await using var conn = new MySqlConnection(BuildAdminConnectionString(connection));
        await conn.OpenAsync(ct);

        await using (var drop = new MySqlCommand($"DROP DATABASE IF EXISTS {quoted};", conn))
        {
            await drop.ExecuteNonQueryAsync(ct);
        }

        await using (var create = new MySqlCommand(
            $"CREATE DATABASE {quoted} CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;", conn))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        _logger.LogInformation("MySQL target database '{Database}' prepared (drop + create)", targetDatabase);
    }

    public async Task RestoreAsync(ConnectionConfig connection, string targetDatabase, string restoreFilePath, CancellationToken ct)
    {
        var mysql = _binaryResolver.Resolve(connection, "mysql");

        var psi = new ProcessStartInfo
        {
            FileName = mysql,
            ArgumentList =
            {
                "-h", connection.Host,
                "-P", connection.Port.ToString(),
                "-u", connection.Username,
                "--default-character-set=utf8mb4",
                targetDatabase,
            },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.Environment["MYSQL_PWD"] = connection.Password;

        using var process = new Process { StartInfo = psi };
        process.Start();
        _logger.LogInformation("mysql process started (PID {Pid})", process.Id);

        using var reg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill mysql process"); }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

        var skipped = 0;
        try
        {
            await using var source = new FileStream(
                restoreFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 65536, useAsync: true);
            using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var writer = process.StandardInput;

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (IsLegacyCreateDatabase(line) || IsLegacyUseDatabase(line))
                {
                    skipped++;
                    continue;
                }

                await writer.WriteLineAsync(line.AsMemory(), ct);
            }
        }
        finally
        {
            process.StandardInput.Close();
        }

        if (skipped > 0)
            _logger.LogInformation(
                "Filtered {Count} legacy CREATE DATABASE / USE statements from dump while restoring into '{Target}'",
                skipped, targetDatabase);

        await Task.WhenAll(stderrTask, stdoutTask);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var stderr = stderrTask.Result.Trim();
            var stdout = stdoutTask.Result.Trim();
            var detail = string.IsNullOrEmpty(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"mysql restore failed (exit code {process.ExitCode}): {detail}");
        }

        _logger.LogInformation("MySQL restore completed for database '{Database}'", targetDatabase);
    }

    private static async Task<(bool HasCreate, bool HasDrop)> ReadPrivilegePairAsync(
        MySqlConnection conn, string sql, string? db, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        if (db is not null)
            cmd.Parameters.AddWithValue("@db", db);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (false, false);

        var hasCreate = !reader.IsDBNull(0) && Convert.ToInt32(reader.GetValue(0)) == 1;
        var hasDrop = !reader.IsDBNull(1) && Convert.ToInt32(reader.GetValue(1)) == 1;
        return (hasCreate, hasDrop);
    }

    private static bool IsLegacyCreateDatabase(string line) =>
        StartsWithToken(line, "CREATE") && HasFollowingToken(line, "CREATE", "DATABASE");

    private static bool IsLegacyUseDatabase(string line) =>
        StartsWithToken(line, "USE");

    private static bool StartsWithToken(string line, string token)
    {
        var i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        if (i + token.Length > line.Length) return false;
        if (string.Compare(line, i, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        var after = i + token.Length;
        return after == line.Length || !char.IsLetterOrDigit(line[after]) && line[after] != '_';
    }

    private static bool HasFollowingToken(string line, string first, string second)
    {
        var i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        i += first.Length;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        if (i + second.Length > line.Length) return false;
        if (string.Compare(line, i, second, 0, second.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        var after = i + second.Length;
        return after == line.Length || !char.IsLetterOrDigit(line[after]) && line[after] != '_';
    }

    private static string BuildAdminConnectionString(ConnectionConfig connection) =>
        new MySqlConnectionStringBuilder
        {
            Server = connection.Host,
            Port = (uint)connection.Port,
            UserID = connection.Username,
            Password = connection.Password,
            Database = "information_schema",
        }.ToString();

    private static string QuoteIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Database name cannot be empty", nameof(name));
        return "`" + name.Replace("`", "``") + "`";
    }
}
