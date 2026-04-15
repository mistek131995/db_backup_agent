using DbBackupAgent.Models;
using DbBackupAgent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbBackupAgent.Workers;

public sealed class BackupWorker : BackgroundService
{
    private readonly BackupJob _job;
    private readonly ScheduleService _schedule;
    private readonly EncryptionService _encryption;
    private readonly List<DatabaseConfig> _databases;
    private readonly ILogger<BackupWorker> _logger;

    public BackupWorker(
        BackupJob job,
        ScheduleService schedule,
        EncryptionService encryption,
        IOptions<List<DatabaseConfig>> databases,
        ILogger<BackupWorker> logger)
    {
        _job = job;
        _schedule = schedule;
        _encryption = encryption;
        _databases = databases.Value;
        _logger = logger;
    }

    private bool IsConfigured()
    {
        if (_databases.Count == 0)
        {
            _logger.LogWarning("BackupWorker: no databases configured. Fill in appsettings.json and restart.");
            return false;
        }

        if (!_encryption.IsConfigured)
        {
            _logger.LogWarning("BackupWorker: encryption key is not configured. Fill in appsettings.json and restart.");
            return false;
        }

        return true;
    }

    /// <summary>How often the worker checks whether a cron occurrence is due.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "BackupWorker started. Databases: {Count}, tick: {TickSec}s, schedule poll: {PollMin} min",
            _databases.Count, TickInterval.TotalSeconds, ScheduleService.PollInterval.TotalMinutes);

        DateTime? lastRun = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nextRun = await _schedule.GetNextRunAsync(stoppingToken);

                if (nextRun is null)
                {
                    _logger.LogDebug("BackupWorker: schedule is inactive, skipping");
                }
                else if (nextRun.Value <= DateTime.UtcNow && (lastRun is null || nextRun > lastRun))
                {
                    lastRun = nextRun;

                    if (!IsConfigured())
                        continue;

                    _logger.LogInformation(
                        "BackupWorker: scheduled run triggered (nextRun={NextRun:u})", nextRun.Value);
                    await RunAllDatabasesAsync(stoppingToken);
                }
                else
                {
                    _logger.LogDebug(
                        "BackupWorker: next run at {NextRun:u}, nothing to do yet", nextRun.Value);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BackupWorker: unexpected error in schedule loop");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("BackupWorker stopped");
    }

    private async Task RunAllDatabasesAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "BackupWorker: starting backup run for {Count} database(s)", _databases.Count);

        int succeeded = 0;
        int failed = 0;

        for (int i = 0; i < _databases.Count; i++)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var config = _databases[i];

            _logger.LogInformation(
                "[{Index}/{Total}] Starting backup. Database: '{Database}', Type: {DatabaseType}",
                i + 1, _databases.Count, config.Database, config.DatabaseType);

            try
            {
                var result = await _job.RunAsync(config, stoppingToken);

                if (result.Success)
                {
                    succeeded++;
                    _logger.LogInformation(
                        "[{Index}/{Total}] Backup succeeded. Database: '{Database}'",
                        i + 1, _databases.Count, config.Database);
                }
                else
                {
                    failed++;
                    _logger.LogError(
                        "[{Index}/{Total}] Backup failed. Database: '{Database}', Error: {ErrorMessage}",
                        i + 1, _databases.Count, config.Database, result.ErrorMessage);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("BackupWorker cancelled during '{Database}'", config.Database);
                return;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex,
                    "[{Index}/{Total}] Unhandled exception for database '{Database}'. Continuing.",
                    i + 1, _databases.Count, config.Database);
            }
        }

        _logger.LogInformation(
            "BackupWorker: run complete. Succeeded: {Succeeded}, Failed: {Failed}, Total: {Total}",
            succeeded, failed, _databases.Count);
    }
}
