using System.Diagnostics;
using DbBackupAgent.Models;
using Microsoft.Extensions.Logging;

namespace DbBackupAgent.Providers;

public sealed class MssqlBackupProvider : IBackupProvider
{
    private readonly ILogger<MssqlBackupProvider> _logger;

    public MssqlBackupProvider(ILogger<MssqlBackupProvider> logger)
    {
        _logger = logger;
    }

    public async Task<BackupResult> BackupAsync(DatabaseConfig config, CancellationToken ct)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{config.Database}_{timestamp}.bak";
        var outputFile = Path.Combine(config.OutputPath, fileName);

        Directory.CreateDirectory(config.OutputPath);

        _logger.LogInformation(
            "Starting MSSQL backup. Database: '{Database}', Host: '{Host}:{Port}', Output: '{OutputFile}'",
            config.Database, config.Host, config.Port, outputFile);

        // T-SQL run via sqlcmd; SQL Server writes the .bak file itself (server-side path)
        var tsql = $"BACKUP DATABASE [{config.Database}] TO DISK = N'{outputFile}' WITH FORMAT, INIT, STATS = 10;";
        var serverAddress = $"{config.Host},{config.Port}";

        var psi = new ProcessStartInfo
        {
            FileName = "sqlcmd",
            ArgumentList =
            {
                "-S", serverAddress,
                "-U", config.Username,
                "-P", config.Password,
                "-Q", tsql
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };

        process.Start();
        _logger.LogInformation("sqlcmd process started (PID {Pid})", process.Id);

        using var reg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill sqlcmd process"); }
        });

        // Read stdout and stderr concurrently to drain the pipes
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);

        sw.Stop();

        var stdoutContent = stdoutTask.Result.Trim();
        var stderrContent = stderrTask.Result.Trim();

        if (stdoutContent.Length > 0)
            _logger.LogDebug("sqlcmd stdout: {Stdout}", stdoutContent);

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrEmpty(stderrContent) ? stdoutContent : stderrContent;
            var message = $"sqlcmd exited with code {process.ExitCode}: {detail}";
            _logger.LogError("sqlcmd failed. ExitCode: {ExitCode}. Detail: {Detail}",
                process.ExitCode, detail);
            throw new InvalidOperationException(message);
        }

        // The .bak is written by SQL Server, so it may not be accessible from this host
        // if SQL Server runs remotely. We report the path and size if reachable.
        long sizeBytes = 0;
        if (File.Exists(outputFile))
        {
            sizeBytes = new FileInfo(outputFile).Length;
        }
        else
        {
            _logger.LogWarning(
                "Backup file '{OutputFile}' is not accessible from this host (SQL Server may be remote). " +
                "The backup was still created on the server.", outputFile);
        }

        _logger.LogInformation(
            "MSSQL backup completed successfully. File: '{FilePath}', Size: {SizeBytes} bytes, Duration: {DurationMs} ms",
            outputFile, sizeBytes, sw.ElapsedMilliseconds);

        return new BackupResult
        {
            FilePath = outputFile,
            SizeBytes = sizeBytes,
            DurationMs = sw.ElapsedMilliseconds,
            Success = true,
        };
    }
}
