using System.Diagnostics;
using System.Text;
using BackupsterAgent.Configuration;
using BackupsterAgent.Exceptions;
using BackupsterAgent.Services.Common.Resolvers;
using Npgsql;

namespace BackupsterAgent.Providers.Restore;

public sealed class PostgresRestoreProvider : IRestoreProvider
{
    private readonly ILogger<PostgresRestoreProvider> _logger;
    private readonly PostgresBinaryResolver _binaryResolver;

    public PostgresRestoreProvider(
        ILogger<PostgresRestoreProvider> logger,
        PostgresBinaryResolver binaryResolver)
    {
        _logger = logger;
        _binaryResolver = binaryResolver;
    }

    public async Task ValidatePermissionsAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct)
    {
        const string sql = @"
SELECT rolcreatedb, rolsuper,
       pg_has_role(current_user, 'pg_signal_backend', 'MEMBER') AS can_signal
FROM pg_roles WHERE rolname = current_user;";

        await using var conn = new NpgsqlConnection(BuildAdminConnectionString(connection));
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            throw new RestorePermissionException(
                $"Пользователь '{connection.Username}' не найден в pg_roles — проверьте корректность credentials для подключения '{connection.Name}'.");
        }

        var rolCreateDb = reader.GetBoolean(0);
        var rolSuper = reader.GetBoolean(1);
        var canSignal = reader.GetBoolean(2);

        if (rolSuper) return;
        if (rolCreateDb && canSignal) return;

        throw new RestorePermissionException(
            $"Пользователь '{connection.Username}' подключения '{connection.Name}' не имеет прав для восстановления БД '{targetDatabase}'. " +
            "Требуются: роль CREATEDB (для DROP+CREATE DATABASE) и pg_signal_backend (для отключения активных соединений), либо superuser. " +
            $"Выдайте права: ALTER ROLE \"{connection.Username}\" WITH CREATEDB; и GRANT pg_signal_backend TO \"{connection.Username}\";.");
    }

    public Task ValidateRestoreSourceAsync(ConnectionConfig connection, string restoreFilePath, CancellationToken ct) =>
        Task.CompletedTask;

    public async Task PrepareTargetDatabaseAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct)
    {
        var quoted = QuoteIdentifier(targetDatabase);

        await using var conn = new NpgsqlConnection(BuildAdminConnectionString(connection));
        await conn.OpenAsync(ct);

        await using (var terminate = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();", conn))
        {
            terminate.Parameters.AddWithValue("db", targetDatabase);
            await terminate.ExecuteNonQueryAsync(ct);
        }

        await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {quoted};", conn))
        {
            await drop.ExecuteNonQueryAsync(ct);
        }

        await using (var create = new NpgsqlCommand($"CREATE DATABASE {quoted};", conn))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        _logger.LogInformation("Postgres target database '{Database}' prepared (drop + create)", targetDatabase);
    }

    public async Task RestoreAsync(ConnectionConfig connection, string targetDatabase, string restoreFilePath, CancellationToken ct)
    {
        var binary = await _binaryResolver.ResolveAsync(connection, "psql", ct);

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            ArgumentList =
            {
                "-h", connection.Host,
                "-p", connection.Port.ToString(),
                "-U", connection.Username,
                "-d", targetDatabase,
                "-v", "ON_ERROR_STOP=1",
                "-f", restoreFilePath,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.EnvironmentVariables["PGPASSWORD"] = connection.Password;
        psi.EnvironmentVariables["LC_MESSAGES"] = "C";
        psi.EnvironmentVariables["LANG"] = "C";

        using var process = new Process { StartInfo = psi };
        process.Start();
        _logger.LogInformation("psql process started (PID {Pid})", process.Id);

        using var reg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill psql process"); }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

        await Task.WhenAll(stderrTask, stdoutTask);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var stderr = stderrTask.Result.Trim();
            var stdout = stdoutTask.Result.Trim();
            var detail = string.IsNullOrEmpty(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"psql restore failed (exit code {process.ExitCode}): {detail}");
        }

        _logger.LogInformation("Postgres restore completed for database '{Database}'", targetDatabase);
    }

    private static string BuildAdminConnectionString(ConnectionConfig connection) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = connection.Host,
            Port = connection.Port,
            Username = connection.Username,
            Password = connection.Password,
            Database = "postgres",
        }.ToString();

    private static string QuoteIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Database name cannot be empty", nameof(name));
        return "\"" + name.Replace("\"", "\"\"") + "\"";
    }
}
