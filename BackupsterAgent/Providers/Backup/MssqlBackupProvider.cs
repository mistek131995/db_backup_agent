using System.Diagnostics;
using System.Text;
using BackupsterAgent.Configuration;
using BackupsterAgent.Domain;
using BackupsterAgent.Services;
using BackupsterAgent.Services.Common;
using Microsoft.Extensions.Logging;

namespace BackupsterAgent.Providers;

public sealed class MssqlBackupProvider : IBackupProvider
{
    private readonly ILogger<MssqlBackupProvider> _logger;

    public MssqlBackupProvider(ILogger<MssqlBackupProvider> logger)
    {
        _logger = logger;
    }

    public async Task<BackupResult> BackupAsync(DatabaseConfig config, ConnectionConfig connection, CancellationToken ct)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{config.Database}_{timestamp}.bak";

        var sqlDir = MssqlSharedPathResolver.GetSqlDir(connection, config.OutputPath);
        var agentDir = MssqlSharedPathResolver.GetAgentDir(connection, config.OutputPath);

        Directory.CreateDirectory(agentDir);

        var sqlFilePath = MssqlSharedPathResolver.JoinSqlPath(sqlDir, fileName);
        var agentFilePath = Path.Combine(agentDir, fileName);

        _logger.LogInformation(
            "Starting MSSQL backup. Database: '{Database}', Host: '{Host}:{Port}', " +
            "SQL path: '{SqlPath}', Agent path: '{AgentPath}'",
            config.Database, connection.Host, connection.Port, sqlFilePath, agentFilePath);

        var tsql = $"BACKUP DATABASE [{config.Database}] TO DISK = N'{sqlFilePath}' WITH FORMAT, INIT, STATS = 10;";
        var serverAddress = $"{connection.Host},{connection.Port}";

        var psi = new ProcessStartInfo
        {
            FileName = "sqlcmd",
            ArgumentList =
            {
                "-S", serverAddress,
                "-U", connection.Username,
                "-P", connection.Password,
                "-C",
                "-Q", tsql
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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

        if (!File.Exists(agentFilePath))
        {
            throw new InvalidOperationException(
                $"Backup file '{agentFilePath}' is not accessible from the agent host. " +
                "Проверьте, что SharedBackupPath и AgentBackupPath указывают на один и тот же каталог.");
        }

        var sizeBytes = new FileInfo(agentFilePath).Length;

        _logger.LogInformation(
            "MSSQL backup completed successfully. File: '{FilePath}', Size: {SizeBytes} bytes, Duration: {DurationMs} ms",
            agentFilePath, sizeBytes, sw.ElapsedMilliseconds);

        return new BackupResult
        {
            FilePath = agentFilePath,
            SizeBytes = sizeBytes,
            DurationMs = sw.ElapsedMilliseconds,
            Success = true,
        };
    }

}
