using System.Diagnostics;
using System.Text;
using BackupsterAgent.Configuration;
using BackupsterAgent.Domain;
using BackupsterAgent.Exceptions;
using BackupsterAgent.Services.Common.Resolvers;
using Npgsql;

namespace BackupsterAgent.Providers.Backup;

public sealed class PostgresPhysicalBackupProvider : IBackupProvider
{
    private readonly ILogger<PostgresPhysicalBackupProvider> _logger;
    private readonly PostgresBinaryResolver _binaryResolver;

    public PostgresPhysicalBackupProvider(
        ILogger<PostgresPhysicalBackupProvider> logger,
        PostgresBinaryResolver binaryResolver)
    {
        _logger = logger;
        _binaryResolver = binaryResolver;
    }

    public async Task ValidatePermissionsAsync(ConnectionConfig connection, string database, CancellationToken ct)
    {
        var binary = await _binaryResolver.ResolveAsync(connection, "pg_basebackup", ct);
        await CheckBinaryAsync(binary, ct);

        var connString = new NpgsqlConnectionStringBuilder
        {
            Host = connection.Host,
            Port = connection.Port,
            Username = connection.Username,
            Password = connection.Password,
            Database = "postgres",
        }.ToString();

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct);

        await using (var cmd = new NpgsqlCommand(
            "SELECT rolsuper, rolreplication FROM pg_roles WHERE rolname = current_user;", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                throw new BackupPermissionException(
                    $"Пользователь '{connection.Username}' не найден в pg_roles — проверьте корректность credentials для подключения '{connection.Name}'.");

            var isSuperuser    = reader.GetBoolean(0);
            var hasReplication = reader.GetBoolean(1);

            if (!isSuperuser && !hasReplication)
                throw new BackupPermissionException(
                    $"Пользователь '{connection.Username}' подключения '{connection.Name}' не имеет прав для physical бэкапа. " +
                    "Требуется привилегия REPLICATION или superuser. " +
                    $"Выдайте права: ALTER ROLE \"{connection.Username}\" WITH REPLICATION;");
        }

        await using (var cmd = new NpgsqlCommand(
            "SELECT string_agg(spcname, ', ' ORDER BY spcname) " +
            "FROM pg_tablespace WHERE spcname NOT IN ('pg_default', 'pg_global');", conn))
        {
            var extraTablespaces = await cmd.ExecuteScalarAsync(ct) as string;
            if (!string.IsNullOrEmpty(extraTablespaces))
                throw new BackupPermissionException(
                    $"Кластер '{connection.Name}' использует tablespaces ({extraTablespaces}), " +
                    "но physical-режим Backupster их не поддерживает: данные tablespace остались бы вне архива, " +
                    "а restore развернул бы кластер с битыми ссылками на пустые каталоги. " +
                    "Используйте logical-режим (BackupMode=Logical) — он не зависит от tablespaces.");
        }

        string pgdata;
        await using (var cmd = new NpgsqlCommand("SHOW data_directory;", conn))
            pgdata = (string?)await cmd.ExecuteScalarAsync(ct) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(pgdata))
            throw new InvalidOperationException(
                $"Не удалось получить путь PGDATA из кластера '{connection.Name}'.");

        _logger.LogInformation("Resolved PGDATA from cluster: '{PgDataPath}'", pgdata);

        if (!Directory.Exists(pgdata))
            throw new InvalidOperationException(
                $"Каталог PGDATA '{pgdata}' недоступен на хосте агента. " +
                "Физический бэкап требует, чтобы агент и PostgreSQL выполнялись на одном хосте. " +
                "Если агент удалённый — используйте режим logical.");
    }

    public async Task<BackupResult> BackupAsync(DatabaseConfig config, ConnectionConfig connection, CancellationToken ct)
    {
        var binary = await _binaryResolver.ResolveAsync(connection, "pg_basebackup", ct);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{config.Database}_{timestamp}.tar.gz";
        var tempDir = Path.Combine(config.OutputPath, $"pgbase-{Guid.NewGuid():N}");
        var outputFile = Path.Combine(config.OutputPath, fileName);

        Directory.CreateDirectory(config.OutputPath);

        _logger.LogInformation(
            "Starting PostgreSQL physical backup (pg_basebackup). Host: '{Host}:{Port}', Output: '{OutputFile}', Binary: '{Binary}'",
            connection.Host, connection.Port, outputFile, binary);

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            ArgumentList =
            {
                "-h", connection.Host,
                "-p", connection.Port.ToString(),
                "-U", connection.Username,
                "--format=tar",
                "--wal-method=fetch",
                "--checkpoint=fast",
                "--gzip",
                "-D", tempDir,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.Environment["PGPASSWORD"] = connection.Password;
        psi.Environment["LC_MESSAGES"] = "C";
        psi.Environment["LANG"] = "C";

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
            _logger.LogInformation("pg_basebackup process started (PID {Pid})", process.Id);

            using var reg = ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill pg_basebackup process"); }
            });

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);
            sw.Stop();

            var stderr = stderrTask.Result.Trim();

            if (process.ExitCode != 0)
            {
                _logger.LogError("pg_basebackup failed. ExitCode: {ExitCode}. Stderr: {Stderr}",
                    process.ExitCode, stderr);
                throw new InvalidOperationException(
                    $"pg_basebackup завершился с кодом {process.ExitCode}: {stderr}");
            }

            var baseTar = Path.Combine(tempDir, "base.tar.gz");
            if (!File.Exists(baseTar))
            {
                var found = Directory.Exists(tempDir)
                    ? string.Join(", ", Directory.GetFiles(tempDir).Select(Path.GetFileName))
                    : "(directory missing)";
                throw new InvalidOperationException(
                    $"pg_basebackup не создал ожидаемый файл 'base.tar.gz'. Найдено: {found}");
            }

            File.Move(baseTar, outputFile, overwrite: true);

            var sizeBytes = new FileInfo(outputFile).Length;
            _logger.LogInformation(
                "PostgreSQL physical backup completed. File: '{FilePath}', Size: {SizeBytes} bytes, Duration: {DurationMs} ms",
                outputFile, sizeBytes, sw.ElapsedMilliseconds);

            return new BackupResult
            {
                FilePath = outputFile,
                SizeBytes = sizeBytes,
                DurationMs = sw.ElapsedMilliseconds,
                Success = true,
            };
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
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
                $"Бинарник {binary} недоступен на хосте агента. " +
                $"Установите пакет postgresql и убедитесь, что {binary} находится в PATH.", ex);
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{binary} --version вернул код {process.ExitCode}. " +
                $"Убедитесь, что пакет postgresql установлен и {binary} находится в PATH.");
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temp directory '{Path}'", path);
        }
    }
}
