using System.Diagnostics;
using System.IO.Compression;
using DbBackupAgent.Models;
using Microsoft.Extensions.Logging;

namespace DbBackupAgent.Providers;

public sealed class PostgresBackupProvider : IBackupProvider
{
    private readonly ILogger<PostgresBackupProvider> _logger;

    public PostgresBackupProvider(ILogger<PostgresBackupProvider> logger)
    {
        _logger = logger;
    }

    public async Task<BackupResult> BackupAsync(DatabaseConfig config, CancellationToken ct)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{config.Database}_{timestamp}.sql.gz";
        var outputFile = Path.Combine(config.OutputPath, fileName);

        Directory.CreateDirectory(config.OutputPath);

        _logger.LogInformation(
            "Starting PostgreSQL backup. Database: '{Database}', Host: '{Host}:{Port}', Output: '{OutputFile}'",
            config.Database, config.Host, config.Port, outputFile);

        var psi = new ProcessStartInfo
        {
            FileName = "pg_dump",
            ArgumentList =
            {
                "-h", config.Host,
                "-p", config.Port.ToString(),
                "-U", config.Username,
                "-F", "p",       // plain SQL format — piped into GZipStream
                config.Database
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Password via environment variable, never via CLI arguments
        psi.Environment["PGPASSWORD"] = config.Password;

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };

        process.Start();
        _logger.LogInformation("pg_dump process started (PID {Pid})", process.Id);

        // Kill the process if the token is cancelled
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

            // Read stderr concurrently to prevent the pipe buffer from blocking
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.StandardOutput.BaseStream.CopyToAsync(gzipStream, ct);
            // GZip is flushed and closed when the using block exits here
            stderrContent = await stderrTask;
        }
        catch
        {
            // Remove the incomplete output file before surfacing the exception
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
            "PostgreSQL backup completed successfully. File: '{FilePath}', Size: {SizeBytes} bytes, Duration: {DurationMs} ms",
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
