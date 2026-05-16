using BackupsterAgent.Configuration;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Common.Resolvers;
using BackupsterAgent.Services.Common.Security;
using BackupsterAgent.Services.Common.State;
using BackupsterAgent.Services.Dashboard.Clients;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Workers;

public sealed class BackupWorker : BackgroundService
{
    private readonly BackupJob _job;
    private readonly ScheduleService _schedule;
    private readonly EncryptionService _encryption;
    private readonly ConnectionResolver _connections;
    private readonly StorageResolver _storages;
    private readonly IAgentActivityLock _activityLock;
    private readonly IBackupRunTracker _runTracker;
    private readonly List<DatabaseConfig> _databases;
    private readonly List<DatabaseConfig> _validDatabases;
    private readonly ILogger<BackupWorker> _logger;

    public BackupWorker(
        BackupJob job,
        ScheduleService schedule,
        EncryptionService encryption,
        ConnectionResolver connections,
        StorageResolver storages,
        IAgentActivityLock activityLock,
        IBackupRunTracker runTracker,
        IOptions<List<DatabaseConfig>> databases,
        ILogger<BackupWorker> logger)
    {
        _job = job;
        _schedule = schedule;
        _encryption = encryption;
        _connections = connections;
        _storages = storages;
        _activityLock = activityLock;
        _runTracker = runTracker;
        _databases = databases.Value;
        _logger = logger;
        _validDatabases = FilterValidDatabases(_databases, _connections, _storages, _logger);
    }

    private static List<DatabaseConfig> FilterValidDatabases(
        List<DatabaseConfig> all,
        ConnectionResolver connections,
        StorageResolver storages,
        ILogger<BackupWorker> logger)
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

            if (string.IsNullOrWhiteSpace(db.StorageName))
            {
                logger.LogError(
                    "BackupWorker: database '{Database}' has empty StorageName, skipping.",
                    db.Database);
                continue;
            }

            if (!storages.TryResolve(db.StorageName, out _))
            {
                logger.LogError(
                    "BackupWorker: database '{Database}' references unknown storage '{StorageName}', skipping. Available: {Available}",
                    db.Database, db.StorageName,
                    storages.Names.Count == 0 ? "(none)" : string.Join(", ", storages.Names));
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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = new List<(DatabaseConfig Config, BackupMode Mode, string StorageName, DateTime NextRun)>();

                foreach (var config in _validDatabases)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    var entries = await _schedule.GetDueSchedulesAsync(config.Database, stoppingToken);

                    if (entries.Count == 0)
                    {
                        _logger.LogDebug(
                            "BackupWorker: no active schedule for '{Database}', skipping", config.Database);
                        continue;
                    }

                    foreach (var entry in entries)
                    {
                        var storageName = !string.IsNullOrWhiteSpace(entry.StorageName)
                            ? entry.StorageName
                            : config.StorageName;

                        var trackerKey = IBackupRunTracker.DatabaseKey(
                            config.Database, entry.Mode, storageName);
                        var last = _runTracker.GetLastRun(trackerKey);
                        if (entry.NextRun <= DateTime.UtcNow && (last is null || entry.NextRun > last))
                        {
                            due.Add((config, entry.Mode, storageName, entry.NextRun));
                            _runTracker.RecordRun(trackerKey, entry.NextRun);
                        }
                        else
                        {
                            _logger.LogDebug(
                                "BackupWorker: '{Database}' ({Mode}, {Storage}) next run at {NextRun:u}, nothing to do yet",
                                config.Database, entry.Mode, storageName, entry.NextRun);
                        }
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
        List<(DatabaseConfig Config, BackupMode Mode, string StorageName, DateTime NextRun)> due,
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "BackupWorker: starting backup run for {Count} due database(s)", due.Count);

        int succeeded = 0;
        int failed = 0;

        for (int i = 0; i < due.Count; i++)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var (config, mode, storageName, nextRun) = due[i];

            _logger.LogInformation(
                "[{Index}/{Total}] Starting backup. Database: '{Database}', Mode: {Mode}, Storage: '{Storage}', Connection: '{Connection}', NextRun: {NextRun:u}",
                i + 1, due.Count, config.Database, mode, storageName, config.ConnectionName, nextRun);

            if (!_storages.TryResolve(storageName, out var storage))
            {
                failed++;
                _logger.LogWarning(
                    "[{Index}/{Total}] Storage '{Storage}' for database '{Database}' is not configured on this agent. Skipping. Available: {Available}",
                    i + 1, due.Count, storageName, config.Database,
                    _storages.Names.Count == 0 ? "(none)" : string.Join(", ", _storages.Names));
                continue;
            }

            try
            {
                BackupResult result;
                using (await _activityLock.AcquireAsync($"backup:{config.Database}:{mode}:{storageName}", stoppingToken))
                {
                    result = await _job.RunAsync(config, storage, mode, stoppingToken);
                }

                if (result.Success)
                {
                    succeeded++;
                    _logger.LogInformation(
                        "[{Index}/{Total}] Backup succeeded. Database: '{Database}', Mode: {Mode}, Storage: '{Storage}'",
                        i + 1, due.Count, config.Database, mode, storageName);
                }
                else
                {
                    failed++;
                    _logger.LogError(
                        "[{Index}/{Total}] Backup failed. Database: '{Database}', Mode: {Mode}, Storage: '{Storage}', Error: {ErrorMessage}",
                        i + 1, due.Count, config.Database, mode, storageName, result.ErrorMessage);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("BackupWorker cancelled during '{Database}' ({Mode}, {Storage})", config.Database, mode, storageName);
                return;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex,
                    "[{Index}/{Total}] Unhandled exception for database '{Database}' ({Mode}, {Storage}). Continuing.",
                    i + 1, due.Count, config.Database, mode, storageName);
            }
        }

        _logger.LogInformation(
            "BackupWorker: run complete. Succeeded: {Succeeded}, Failed: {Failed}, Total: {Total}",
            succeeded, failed, due.Count);
    }

}
