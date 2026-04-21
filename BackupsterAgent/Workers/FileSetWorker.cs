using BackupsterAgent.Configuration;
using BackupsterAgent.Domain;
using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Dashboard;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Workers;

public sealed class FileSetWorker : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly FileSetBackupJob _job;
    private readonly ScheduleService _schedule;
    private readonly EncryptionService _encryption;
    private readonly StorageResolver _storages;
    private readonly IAgentActivityLock _activityLock;
    private readonly IBackupRunTracker _runTracker;
    private readonly List<FileSetConfig> _fileSets;
    private readonly List<FileSetConfig> _validFileSets;
    private readonly ILogger<FileSetWorker> _logger;

    public FileSetWorker(
        FileSetBackupJob job,
        ScheduleService schedule,
        EncryptionService encryption,
        StorageResolver storages,
        IAgentActivityLock activityLock,
        IBackupRunTracker runTracker,
        IOptions<List<FileSetConfig>> fileSets,
        ILogger<FileSetWorker> logger)
    {
        _job = job;
        _schedule = schedule;
        _encryption = encryption;
        _storages = storages;
        _activityLock = activityLock;
        _runTracker = runTracker;
        _fileSets = fileSets.Value;
        _logger = logger;
        _validFileSets = FilterValid(_fileSets, _storages, _logger);
    }

    private static List<FileSetConfig> FilterValid(
        List<FileSetConfig> all,
        StorageResolver storages,
        ILogger<FileSetWorker> logger)
    {
        var valid = new List<FileSetConfig>(all.Count);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fs in all)
        {
            if (string.IsNullOrWhiteSpace(fs.Name))
            {
                logger.LogError("FileSetWorker: file set with empty Name, skipping.");
                continue;
            }

            if (!seenNames.Add(fs.Name))
            {
                logger.LogError(
                    "FileSetWorker: duplicate file set name '{Name}', skipping second occurrence.", fs.Name);
                continue;
            }

            if (string.IsNullOrWhiteSpace(fs.StorageName))
            {
                logger.LogError(
                    "FileSetWorker: file set '{Name}' has empty StorageName, skipping.", fs.Name);
                continue;
            }

            if (!storages.TryResolve(fs.StorageName, out _))
            {
                logger.LogError(
                    "FileSetWorker: file set '{Name}' references unknown storage '{StorageName}', skipping. Available: {Available}",
                    fs.Name, fs.StorageName,
                    storages.Names.Count == 0 ? "(none)" : string.Join(", ", storages.Names));
                continue;
            }

            if (fs.Paths.Count == 0)
            {
                logger.LogError(
                    "FileSetWorker: file set '{Name}' has no Paths configured, skipping.", fs.Name);
                continue;
            }

            valid.Add(fs);
        }

        return valid;
    }

    private bool IsConfigured()
    {
        if (_validFileSets.Count == 0)
            return false;

        if (!_encryption.IsConfigured)
        {
            _logger.LogWarning("FileSetWorker: encryption key is not configured. Fill in appsettings.json and restart.");
            return false;
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_fileSets.Count == 0)
        {
            _logger.LogInformation("FileSetWorker: no file sets configured, worker idle.");
            return;
        }

        _logger.LogInformation(
            "FileSetWorker started. FileSets: {Count} (runnable: {Runnable}), tick: {TickSec}s",
            _fileSets.Count, _validFileSets.Count, TickInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = new List<(FileSetConfig Config, DateTime NextRun)>();

                foreach (var config in _validFileSets)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    var scheduleKey = config.Name;
                    var trackerKey = IBackupRunTracker.FileSetKey(config.Name);

                    var nextRun = await _schedule.GetNextRunAsync(scheduleKey, stoppingToken);

                    if (nextRun is null)
                    {
                        _logger.LogDebug(
                            "FileSetWorker: schedule inactive for '{Name}', skipping", config.Name);
                        continue;
                    }

                    var last = _runTracker.GetLastRun(trackerKey);
                    if (nextRun.Value <= DateTime.UtcNow && (last is null || nextRun.Value > last))
                    {
                        due.Add((config, nextRun.Value));
                        _runTracker.RecordRun(trackerKey, nextRun.Value);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "FileSetWorker: '{Name}' next run at {NextRun:u}, nothing to do yet",
                            config.Name, nextRun.Value);
                    }
                }

                if (due.Count > 0 && IsConfigured())
                    await RunDueAsync(due, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FileSetWorker: unexpected error in schedule loop");
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

        _logger.LogInformation("FileSetWorker stopped");
    }

    private async Task RunDueAsync(
        List<(FileSetConfig Config, DateTime NextRun)> due,
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "FileSetWorker: starting run for {Count} due file set(s)", due.Count);

        int succeeded = 0;
        int failed = 0;

        for (int i = 0; i < due.Count; i++)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var (config, nextRun) = due[i];

            _logger.LogInformation(
                "[{Index}/{Total}] Starting file set backup. Name: '{Name}', Storage: '{Storage}', NextRun: {NextRun:u}",
                i + 1, due.Count, config.Name, config.StorageName, nextRun);

            try
            {
                BackupResult result;
                using (await _activityLock.AcquireAsync($"fileset:{config.Name}", stoppingToken))
                {
                    result = await _job.RunAsync(config, stoppingToken);
                }

                if (result.Success)
                {
                    succeeded++;
                    _logger.LogInformation(
                        "[{Index}/{Total}] File set backup succeeded. Name: '{Name}'",
                        i + 1, due.Count, config.Name);
                }
                else
                {
                    failed++;
                    _logger.LogError(
                        "[{Index}/{Total}] File set backup failed. Name: '{Name}', Error: {ErrorMessage}",
                        i + 1, due.Count, config.Name, result.ErrorMessage);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("FileSetWorker cancelled during '{Name}'", config.Name);
                return;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex,
                    "[{Index}/{Total}] Unhandled exception for file set '{Name}'. Continuing.",
                    i + 1, due.Count, config.Name);
            }
        }

        _logger.LogInformation(
            "FileSetWorker: run complete. Succeeded: {Succeeded}, Failed: {Failed}, Total: {Total}",
            succeeded, failed, due.Count);
    }
}
