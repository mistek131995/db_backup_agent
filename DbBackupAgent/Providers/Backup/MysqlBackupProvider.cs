using System.Diagnostics;
using System.IO.Compression;
using DbBackupAgent.Configuration;
using DbBackupAgent.Domain;
using Microsoft.Extensions.Logging;

namespace DbBackupAgent.Providers;

public sealed class MysqlBackupProvider : IBackupProvider
{
    private readonly ILogger<MysqlBackupProvider> _logger;

    public MysqlBackupProvider(ILogger<MysqlBackupProvider> logger)
    {
        _logger = logger;
    }

    public async Task<BackupResult> BackupAsync(DatabaseConfig config, ConnectionConfig connection, CancellationToken ct)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{config.Database}_{timestamp}.sql.gz";
        var outputFile = Path.Combine(config.OutputPath, fileName);

        Directory.CreateDirectory(config.OutputPath);

        _logger.LogInformation(
            "Starting MySQL backup. Database: '{Database}', Host: '{Host}:{Port}', Output: '{OutputFile}'",
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
            "MySQL backup completed successfully. File: '{FilePath}', Size: {SizeBytes} bytes, Duration: {DurationMs} ms",
            outputFile, fileInfo.Length, sw.ElapsedMilliseconds);

        return new BackupResult
        {
            FilePath = outputFile,
            SizeBytes = fileInfo.Length,
            DurationMs = sw.ElapsedMilliseconds,
            Success = true,
        };
    }

    private void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not delete partial file '{Path}'", path); }
    }
}
