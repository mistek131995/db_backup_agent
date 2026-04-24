using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using BackupsterAgent.Configuration;
using BackupsterAgent.Domain;
using BackupsterAgent.Exceptions;
using BackupsterAgent.Services.Common.Resolvers;
using Npgsql;

namespace BackupsterAgent.Providers.Backup;

public sealed class PostgresLogicalBackupProvider : IBackupProvider
{
    private readonly ILogger<PostgresLogicalBackupProvider> _logger;
    private readonly PostgresBinaryResolver _binaryResolver;

    public PostgresLogicalBackupProvider(
        ILogger<PostgresLogicalBackupProvider> logger,
        PostgresBinaryResolver binaryResolver)
    {
        _logger = logger;
        _binaryResolver = binaryResolver;
    }

    public async Task ValidatePermissionsAsync(ConnectionConfig connection, string database, CancellationToken ct)
    {
        var binary = await _binaryResolver.ResolveAsync(connection, "pg_dump", ct);
        await CheckBinaryAsync(binary, ct);

        const string sql = @"
SELECT rolsuper,
       has_database_privilege(current_user, current_database(), 'CONNECT') AS can_connect,
       has_schema_privilege(current_user, 'public', 'USAGE') AS can_use_public
FROM pg_roles WHERE rolname = current_user;";

        var connString = new NpgsqlConnectionStringBuilder
        {
            Host = connection.Host,
            Port = connection.Port,
            Username = connection.Username,
            Password = connection.Password,
            Database = database,
        }.ToString();

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            throw new BackupPermissionException(
                $"Пользователь '{connection.Username}' не найден в pg_roles — проверьте корректность credentials для подключения '{connection.Name}'.");

        var isSuperuser  = reader.GetBoolean(0);
        var canConnect   = reader.GetBoolean(1);
        var canUsePublic = reader.GetBoolean(2);

        if (!isSuperuser && !(canConnect && canUsePublic))
            throw new BackupPermissionException(
                $"Пользователь '{connection.Username}' подключения '{connection.Name}' не имеет прав для бэкапа БД '{database}'. " +
                "Требуется superuser, либо CONNECT на БД и USAGE на схему public. " +
                $"Выдайте права: GRANT CONNECT ON DATABASE \"{database}\" TO \"{connection.Username}\"; " +
                $"GRANT USAGE ON SCHEMA public TO \"{connection.Username}\";");
    }

    public async Task<BackupResult> BackupAsync(DatabaseConfig config, ConnectionConfig connection, CancellationToken ct)
    {
        var binary = await _binaryResolver.ResolveAsync(connection, "pg_dump", ct);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{config.Database}_{timestamp}.sql.gz";
        var outputFile = Path.Combine(config.OutputPath, fileName);

        Directory.CreateDirectory(config.OutputPath);

        _logger.LogInformation(
            "Starting PostgreSQL logical backup. Database: '{Database}', Host: '{Host}:{Port}', Output: '{OutputFile}', Binary: '{Binary}'",
            config.Database, connection.Host, connection.Port, outputFile, binary);

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            ArgumentList =
            {
                "-h", connection.Host,
                "-p", connection.Port.ToString(),
                "-U", connection.Username,
                "-F", "p",
                "--clean",
                "--if-exists",
                config.Database
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.Environment["PGPASSWORD"] = connection.Password;
        psi.Environment["LC_MESSAGES"] = "C";
        psi.Environment["LANG"] = "C";

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };

        process.Start();
        _logger.LogInformation("pg_dump process started (PID {Pid})", process.Id);

        using var reg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill pg_dump process"); }
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
            var message = $"pg_dump exited with code {process.ExitCode}: {stderrContent.Trim()}";
            _logger.LogError("pg_dump failed. ExitCode: {ExitCode}. Stderr: {Stderr}",
                process.ExitCode, stderrContent.Trim());
            throw new InvalidOperationException(message);
        }

        var fileInfo = new FileInfo(outputFile);
        _logger.LogInformation(
            "PostgreSQL logical backup completed successfully. File: '{FilePath}', Size: {SizeBytes} bytes, Duration: {DurationMs} ms",
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

        psi.Environment["LC_MESSAGES"] = "C";
        psi.Environment["LANG"] = "C";

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
                $"Install the postgresql-client package and ensure {binary} is in PATH.", ex);
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{binary} --version returned exit code {process.ExitCode}. " +
                $"Ensure the postgresql-client package is installed and {binary} is in PATH.");
    }

    private void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not delete partial file '{Path}'", path); }
    }
}
