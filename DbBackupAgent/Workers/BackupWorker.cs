using DbBackupAgent.Configuration;
using DbBackupAgent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbBackupAgent.Workers;

public sealed class BackupWorker : BackgroundService
{
    private readonly BackupJob _job;
    private readonly ScheduleService _schedule;
    private readonly EncryptionService _encryption;
    private readonly ConnectionResolver _connections;
    private readonly List<DatabaseConfig> _databases;
    private readonly List<DatabaseConfig> _validDatabases;
    private readonly ILogger<BackupWorker> _logger;

    public BackupWorker(
        BackupJob job,
        ScheduleService schedule,
        EncryptionService encryption,
        ConnectionResolver connections,
        IOptions<List<DatabaseConfig>> databases,
        ILogger<BackupWorker> logger)
    {
        _job = job;
        _schedule = schedule;
        _encryption = encryption;
        _connections = connections;
        _databases = databases.Value;
        _logger = logger;
        _validDatabases = FilterValidDatabases(_databases, _connections, _logger);
    }

    private static List<DatabaseConfig> FilterValidDatabases(
        List<DatabaseConfig> all, ConnectionResolver connections, ILogger<BackupWorker> logger)
    {
        var valid = new List<DatabaseConfig>(all.Count);

        foreach (var db in all)
        {
            if (string.IsNullOrWhiteSpace(db.ConnectionName))
            {
                logger.LogError(
                    "BackupWorker: database '{Database}' has empty ConnectionName, skipping.",
                    db.Database);
                continue;
            }

            if (!connections.TryResolve(db.ConnectionName, out _))
            {
                logger.LogError(
                    "BackupWorker: database '{Database}' references unknown connection '{ConnectionName}', skipping. Available: {Available}",
                    db.Database, db.ConnectionName,
                    connections.Names.Count == 0 ? "(none)" : string.Join(", ", connections.Names));
                continue;
            }

            valid.Add(db);
        }

        return valid;
    }

    private bool IsConfigured()
    {
        if (_validDatabases.Count == 0)
        {
            _logger.LogWarning("BackupWorker: no runnable databases (check Connections/Databases in appsettings.json).");
            return false;
        }

        if (!_encryption.IsConfigured)
        {
            _logger.LogWarning("BackupWorker: encryption key is not configured. Fill in appsettings.json and restart.");
            return false;
        }

        return true;
    }

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "BackupWorker started. Connections: {ConnCount}, Databases: {DbCount} (runnable: {RunnableCount}), tick: {TickSec}s, schedule poll: {PollMin} min",
            _connections.Names.Count, _databases.Count, _validDatabases.Count,
            TickInterval.TotalSeconds, ScheduleService.PollInterval.TotalMinutes);

        var lastRunByDb = new Dictionary<string, DateTime?>(StringComparer.Ordinal);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = new List<(DatabaseConfig config, DateTime nextRun)>();

                foreach (var config in _validDatabases)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    var nextRun = await _schedule.GetNextRunAsync(config.Database, stoppingToken);

                    if (nextRun is null)
                    {
                        _logger.LogDebug(
                            "BackupWorker: schedule is inactive for '{Database}', skipping", config.Database);
                        continue;
                    }

                    var last = lastRunByDb.GetValueOrDefault(config.Database);
                    if (nextRun.Value <= DateTime.UtcNow && (last is null || nextRun.Value > last))
                    {
                        due.Add((config, nextRun.Value));
                        lastRunByDb[config.Database] = nextRun.Value;
                    }
                    else
                    {
                        _logger.LogDebug(
                            "BackupWorker: '{Database}' next run at {NextRun:u}, nothing to do yet",
                            config.Database, nextRun.Value);
                    }
                }

                if (due.Count > 0 && IsConfigured())
                    await RunDueDatabasesAsync(due, stoppingToken);
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

    private async Task RunDueDatabasesAsync(
        List<(DatabaseConfig config, DateTime nextRun)> due,
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "BackupWorker: starting backup run for {Count} due database(s)", due.Count);

        int succeeded = 0;
        int failed = 0;

        for (int i = 0; i < due.Count; i++)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var (config, nextRun) = due[i];

            _logger.LogInformation(
                "[{Index}/{Total}] Starting backup. Database: '{Database}', Connection: '{Connection}', NextRun: {NextRun:u}",
                i + 1, due.Count, config.Database, config.ConnectionName, nextRun);

            try
            {
                var result = await _job.RunAsync(config, stoppingToken);

                if (result.Success)
                {
                    succeeded++;
                    _logger.LogInformation(
                        "[{Index}/{Total}] Backup succeeded. Database: '{Database}'",
                        i + 1, due.Count, config.Database);
                }
                else
                {
                    failed++;
                    _logger.LogError(
                        "[{Index}/{Total}] Backup failed. Database: '{Database}', Error: {ErrorMessage}",
                        i + 1, due.Count, config.Database, result.ErrorMessage);
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
                    i + 1, due.Count, config.Database);
            }
        }

        _logger.LogInformation(
            "BackupWorker: run complete. Succeeded: {Succeeded}, Failed: {Failed}, Total: {Total}",
            succeeded, failed, due.Count);
    }
}
