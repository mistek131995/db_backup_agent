using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using BackupsterAgent.Configuration;
using BackupsterAgent.Domain;
using BackupsterAgent.Exceptions;
using MySqlConnector;

namespace BackupsterAgent.Providers.Backup;

public sealed class MysqlLogicalBackupProvider : IBackupProvider
{
    private readonly ILogger<MysqlLogicalBackupProvider> _logger;

    public MysqlLogicalBackupProvider(ILogger<MysqlLogicalBackupProvider> logger)
    {
        _logger = logger;
    }

    public async Task ValidatePermissionsAsync(ConnectionConfig connection, string database, CancellationToken ct)
    {
        await ValidateBackupPermissionsAsync(connection, database, ct);
    }

    private static async Task ValidateBackupPermissionsAsync(ConnectionConfig connection, string database, CancellationToken ct)
    {
        await CheckBinaryAsync("mysqldump", ct);

        const string globalSql = @"
SELECT
  MAX(CASE WHEN PRIVILEGE_TYPE IN ('SELECT', 'ALL PRIVILEGES') THEN 1 ELSE 0 END) AS has_select,
  MAX(CASE WHEN PRIVILEGE_TYPE IN ('PROCESS', 'ALL PRIVILEGES') THEN 1 ELSE 0 END) AS has_process
FROM information_schema.USER_PRIVILEGES;";

        const string schemaSql = @"
SELECT
  MAX(CASE WHEN PRIVILEGE_TYPE IN ('SELECT', 'ALL PRIVILEGES') THEN 1 ELSE 0 END) AS has_select
FROM information_schema.SCHEMA_PRIVILEGES
WHERE TABLE_SCHEMA = @db;";

        await using var conn = new MySqlConnection(BuildConnectionString(connection));
        await conn.OpenAsync(ct);

        bool globalSelect, globalProcess;
        await using (var cmd = new MySqlCommand(globalSql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                throw new BackupPermissionException("Не удалось прочитать данные о правах пользователя.");
            globalSelect  = reader.GetInt32(0) == 1;
            globalProcess = reader.GetInt32(1) == 1;
        }

        var hasSelect = globalSelect;
        if (!hasSelect)
        {
            await using var cmd2 = new MySqlCommand(schemaSql, conn);
            cmd2.Parameters.AddWithValue("@db", database);
            await using var reader2 = await cmd2.ExecuteReaderAsync(ct);
            hasSelect = await reader2.ReadAsync(ct) && reader2.GetInt32(0) == 1;
        }

        if (!hasSelect)
            throw new BackupPermissionException(
                $"Пользователь '{connection.Username}' подключения '{connection.Name}' не имеет права SELECT на БД '{database}'. " +
                $"Выдайте права: GRANT SELECT ON `{database}`.* TO '{connection.Username}'@'%'; FLUSH PRIVILEGES;");

        if (!globalProcess)
            throw new BackupPermissionException(
                $"Пользователь '{connection.Username}' подключения '{connection.Name}' не имеет глобальной привилегии PROCESS, " +
                "необходимой для --single-transaction (mysqldump). " +
                $"Выдайте права: GRANT PROCESS ON *.* TO '{connection.Username}'@'%'; FLUSH PRIVILEGES;");
    }

    public async Task<BackupResult> BackupAsync(DatabaseConfig config, ConnectionConfig connection, CancellationToken ct)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{config.Database}_{timestamp}.sql.gz";
        var outputFile = Path.Combine(config.OutputPath, fileName);

        Directory.CreateDirectory(config.OutputPath);

        _logger.LogInformation(
            "Starting MySQL logical backup. Database: '{Database}', Host: '{Host}:{Port}', Output: '{OutputFile}'",
            config.Database, connection.Host, connection.Port, outputFile);

        var psi = new ProcessStartInfo
        {
            FileName = "mysqldump",
            ArgumentList =
            {
                "-h", connection.Host,
                "-P", connection.Port.ToString(),
                "-u", connection.Username,
                "--single-transaction",
                "--quick",
                "--routines",
                "--triggers",
                "--events",
                "--hex-blob",
                "--set-gtid-purged=OFF",
                "--default-character-set=utf8mb4",
                "--databases", config.Database,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.Environment["MYSQL_PWD"] = connection.Password;

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };

        process.Start();
        _logger.LogInformation("mysqldump process started (PID {Pid})", process.Id);

        using var reg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill mysqldump process"); }
        });

        string stderrContent;
        try
        {
            await using var fileStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 65536, useAsync: true);
            await using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);

            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.StandardOutput.BaseStream.CopyToAsync(gzipStream, ct);
            stderrContent = await stderrTask;
        }
        catch
        {
            TryDeleteFile(outputFile);
            throw;
        }

        await process.WaitForExitAsync(ct);
        sw.Stop();

        if (process.ExitCode != 0)
        {
            TryDeleteFile(outputFile);
            var message = $"mysqldump exited with code {process.ExitCode}: {stderrContent.Trim()}";
            _logger.LogError("mysqldump failed. ExitCode: {ExitCode}. Stderr: {Stderr}",
                process.ExitCode, stderrContent.Trim());
            throw new InvalidOperationException(message);
        }

        var fileInfo = new FileInfo(outputFile);
        _logger.LogInformation(
            "MySQL logical backup completed successfully. File: '{FilePath}', Size: {SizeBytes} bytes, Duration: {DurationMs} ms",
            outputFile, fileInfo.Length, sw.ElapsedMilliseconds);

        return new BackupResult
        {
            FilePath = outputFile,
            SizeBytes = fileInfo.Length,
            DurationMs = sw.ElapsedMilliseconds,
            Success = true,
        };
    }

    private static async Task CheckBinaryAsync(string binary, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            ArgumentList = { "--version" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            await process.WaitForExitAsync(ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{binary} is not available on this host. " +
                $"Install the mysql-client package and ensure {binary} is in PATH.", ex);
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{binary} --version returned exit code {process.ExitCode}. " +
                $"Ensure the mysql-client package is installed and {binary} is in PATH.");
    }

    private static string BuildConnectionString(ConnectionConfig connection) =>
        new MySqlConnectionStringBuilder
        {
            Server = connection.Host,
            Port = (uint)connection.Port,
            UserID = connection.Username,
            Password = connection.Password,
        }.ToString();

    private void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not delete partial file '{Path}'", path); }
    }
}
