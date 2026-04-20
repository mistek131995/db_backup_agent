using BackupsterAgent.Configuration;
using BackupsterAgent.Contracts;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Dashboard;
using BackupsterAgent.Services.Delete;
using BackupsterAgent.Services.Restore;
using BackupsterAgent.Services.Upload;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Workers;

public sealed class AgentTaskPollingService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly IAgentTaskClient _client;
    private readonly DatabaseRestoreService _databaseRestore;
    private readonly FileRestoreService _fileRestore;
    private readonly BackupDeleteService _backupDelete;
    private readonly BackupJob _backupJob;
    private readonly IBackupRunTracker _runTracker;
    private readonly IAgentActivityLock _activityLock;
    private readonly IProgressReporterFactory _reporterFactory;
    private readonly IUploadServiceFactory _uploadFactory;
    private readonly List<DatabaseConfig> _databases;
    private readonly RestoreSettings _restoreSettings;
    private readonly ILogger<AgentTaskPollingService> _logger;

    public AgentTaskPollingService(
        IAgentTaskClient client,
        DatabaseRestoreService databaseRestore,
        FileRestoreService fileRestore,
        BackupDeleteService backupDelete,
        BackupJob backupJob,
        IBackupRunTracker runTracker,
        IAgentActivityLock activityLock,
        IProgressReporterFactory reporterFactory,
        IUploadServiceFactory uploadFactory,
        IOptions<List<DatabaseConfig>> databases,
        IOptions<RestoreSettings> restoreSettings,
        ILogger<AgentTaskPollingService> logger)
    {
        _client = client;
        _databaseRestore = databaseRestore;
        _fileRestore = fileRestore;
        _backupDelete = backupDelete;
        _backupJob = backupJob;
        _runTracker = runTracker;
        _activityLock = activityLock;
        _reporterFactory = reporterFactory;
        _uploadFactory = uploadFactory;
        _databases = databases.Value;
        _restoreSettings = restoreSettings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "AgentTaskPollingService started. Poll interval: {PollSec}s, initial backoff: {BackoffSec}s, max backoff: {MaxSec}s",
            PollInterval.TotalSeconds, InitialBackoff.TotalSeconds, MaxBackoff.TotalSeconds);

        CleanupOrphanTemp();

        var backoff = InitialBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var task = await _client.FetchTaskAsync(stoppingToken);
                backoff = InitialBackoff;

                if (task is null)
                {
                    if (!await DelayOrCancel(PollInterval, stoppingToken)) break;
                    continue;
                }

                bool cancelled = false;
                using (await _activityLock.AcquireAsync($"task:{task.Type}:{task.Id}", stoppingToken))
                {
                    PatchAgentTaskDto patch;
                    try
                    {
                        patch = await ExecuteTaskAsync(task, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(
                            "AgentTaskPollingService: task {TaskId} cancelled mid-pipeline", task.Id);
                        cancelled = true;
                        patch = new PatchAgentTaskDto
                        {
                            Status = AgentTaskStatus.Failed,
                            ErrorMessage = "Задача прервана: агент остановлен.",
                            Restore = task.Type == AgentTaskType.Restore
                                ? new RestoreTaskResult { DatabaseStatus = RestoreDatabaseStatus.Failed }
                                : null,
                        };
                    }

                    using var finalizeCts = cancelled ? new CancellationTokenSource(TimeSpan.FromSeconds(10)) : null;
                    var patchCt = cancelled ? finalizeCts!.Token : stoppingToken;

                    try
                    {
                        await _client.PatchTaskAsync(task.Id, patch, patchCt);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "AgentTaskPollingService: failed to PATCH final status for task {TaskId}. " +
                            "Task will be marked in_progress until sweeper picks it up.", task.Id);
                    }
                }

                if (cancelled) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "AgentTaskPollingService: polling error, backing off {BackoffSec}s",
                    backoff.TotalSeconds);

                if (!await DelayOrCancel(backoff, stoppingToken)) break;

                var next = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
                backoff = next;
            }
        }

        _logger.LogInformation("AgentTaskPollingService stopped");
    }

    private Task<PatchAgentTaskDto> ExecuteTaskAsync(AgentTaskForAgentDto task, CancellationToken ct) =>
        task.Type switch
        {
            AgentTaskType.Restore => ExecuteRestoreAsync(task, ct),
            AgentTaskType.Delete => ExecuteDeleteAsync(task, ct),
            AgentTaskType.Backup => ExecuteBackupAsync(task, ct),
            _ => Task.FromResult(RejectUnsupported(task, task.Type.ToString())),
        };

    private PatchAgentTaskDto RejectUnsupported(AgentTaskForAgentDto task, string typeName)
    {
        _logger.LogWarning(
            "AgentTaskPollingService: task {TaskId} has unsupported type '{Type}' — this agent version handles only Restore.",
            task.Id, typeName);
        return new PatchAgentTaskDto
        {
            Status = AgentTaskStatus.Failed,
            ErrorMessage = $"Тип задачи '{typeName}' не поддерживается этой версией агента.",
        };
    }

    private async Task<PatchAgentTaskDto> ExecuteRestoreAsync(AgentTaskForAgentDto task, CancellationToken ct)
    {
        if (task.Restore is null)
        {
            _logger.LogWarning(
                "AgentTaskPollingService: restore task {TaskId} has empty payload.", task.Id);
            return new PatchAgentTaskDto
            {
                Status = AgentTaskStatus.Failed,
                ErrorMessage = "Сервер не передал тело restore-задачи.",
                Restore = new RestoreTaskResult { DatabaseStatus = RestoreDatabaseStatus.Failed },
            };
        }

        var payload = task.Restore;

        _logger.LogInformation(
            "AgentTaskPollingService: executing restore task {TaskId} (source '{Source}', target '{Target}')",
            task.Id, payload.SourceDatabaseName, payload.TargetDatabaseName ?? payload.SourceDatabaseName);

        if (ValidateTaskNames(payload) is { } validationError)
        {
            _logger.LogWarning(
                "AgentTaskPollingService: restore task {TaskId} rejected by name validation: {Reason}",
                task.Id, validationError);
            return FailRestore(validationError);
        }

        await using var reporter = _reporterFactory.CreateForRestore(task.Id);

        IUploadService uploader;
        try
        {
            uploader = ResolveUploader(payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AgentTaskPollingService: failed to resolve storage for task {TaskId}", task.Id);
            return FailRestore(ex.Message);
        }

        var dbResult = await _databaseRestore.RunAsync(task.Id, payload, uploader, reporter, ct);

        var fileResult = payload.ManifestKey is null
            ? FileRestoreResult.Skipped()
            : await _fileRestore.RunAsync(payload.ManifestKey, payload.TargetFileRoot, uploader, reporter, ct);

        return CombineResults(dbResult, fileResult);
    }

    private static PatchAgentTaskDto FailRestore(string message) => new()
    {
        Status = AgentTaskStatus.Failed,
        ErrorMessage = message,
        Restore = new RestoreTaskResult { DatabaseStatus = RestoreDatabaseStatus.Failed },
    };

    private async Task<PatchAgentTaskDto> ExecuteDeleteAsync(AgentTaskForAgentDto task, CancellationToken ct)
    {
        if (task.Delete is null)
        {
            _logger.LogWarning(
                "AgentTaskPollingService: delete task {TaskId} has empty payload.", task.Id);
            return new PatchAgentTaskDto
            {
                Status = AgentTaskStatus.Failed,
                ErrorMessage = "Сервер не передал тело delete-задачи.",
            };
        }

        var payload = task.Delete;

        _logger.LogInformation(
            "AgentTaskPollingService: executing delete task {TaskId} (storage '{Storage}')",
            task.Id, payload.StorageName);

        await using var reporter = _reporterFactory.CreateForDelete(task.Id);

        var result = await _backupDelete.RunAsync(task.Id, payload, reporter, ct);

        return result.IsSuccess
            ? new PatchAgentTaskDto { Status = AgentTaskStatus.Success }
            : new PatchAgentTaskDto
            {
                Status = AgentTaskStatus.Failed,
                ErrorMessage = result.ErrorMessage,
            };
    }

    private async Task<PatchAgentTaskDto> ExecuteBackupAsync(AgentTaskForAgentDto task, CancellationToken ct)
    {
        if (task.Backup is null || string.IsNullOrWhiteSpace(task.Backup.DatabaseName))
        {
            _logger.LogWarning(
                "AgentTaskPollingService: backup task {TaskId} has empty payload.", task.Id);
            return new PatchAgentTaskDto
            {
                Status = AgentTaskStatus.Failed,
                ErrorMessage = "Сервер не передал имя БД для backup-задачи.",
            };
        }

        var databaseName = task.Backup.DatabaseName;

        var config = _databases.FirstOrDefault(
            d => string.Equals(d.Database, databaseName, StringComparison.Ordinal));

        if (config is null)
        {
            _logger.LogWarning(
                "AgentTaskPollingService: backup task {TaskId} references unknown database '{Database}'",
                task.Id, databaseName);
            return new PatchAgentTaskDto
            {
                Status = AgentTaskStatus.Failed,
                ErrorMessage = $"БД '{databaseName}' не найдена в конфиге агента.",
            };
        }

        _logger.LogInformation(
            "AgentTaskPollingService: executing backup task {TaskId} for database '{Database}'",
            task.Id, databaseName);

        BackupResult result;
        try
        {
            result = await _backupJob.RunAsync(config, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AgentTaskPollingService: backup task {TaskId} threw", task.Id);
            return new PatchAgentTaskDto
            {
                Status = AgentTaskStatus.Failed,
                ErrorMessage = $"Неожиданная ошибка бэкапа: {ex.Message}",
            };
        }

        _runTracker.RecordRun(databaseName, DateTime.UtcNow);

        return result.Success
            ? new PatchAgentTaskDto
            {
                Status = AgentTaskStatus.Success,
                Backup = new BackupTaskResult { BackupRecordId = result.BackupRecordId },
            }
            : new PatchAgentTaskDto
            {
                Status = AgentTaskStatus.Failed,
                ErrorMessage = result.ErrorMessage,
                Backup = new BackupTaskResult { BackupRecordId = result.BackupRecordId },
            };
    }

    private void CleanupOrphanTemp()
    {
        var tempRoot = DatabaseRestoreService.BuildTempRoot(_restoreSettings.TempPath);
        try
        {
            if (!Directory.Exists(tempRoot))
            {
                _logger.LogDebug("Restore temp root '{TempRoot}' does not exist, nothing to clean.", tempRoot);
                return;
            }

            var entries = Directory.EnumerateFileSystemEntries(tempRoot).ToList();
            if (entries.Count == 0)
            {
                _logger.LogDebug("Restore temp root '{TempRoot}' is already clean.", tempRoot);
                return;
            }

            _logger.LogInformation(
                "Cleaning {Count} orphan entries from restore temp root '{TempRoot}'",
                entries.Count, tempRoot);

            foreach (var entry in entries)
            {
                try
                {
                    if (Directory.Exists(entry))
                        Directory.Delete(entry, recursive: true);
                    else
                        File.Delete(entry);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete orphan temp entry '{Entry}'", entry);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean restore temp root '{TempRoot}'", tempRoot);
        }
    }

    internal static string? ValidateTaskNames(RestoreTaskPayload payload)
    {
        if (!DatabaseNameValidator.IsValid(payload.SourceDatabaseName, out var sourceReason))
            return $"Имя исходной БД не прошло валидацию: {sourceReason}.";

        if (!string.IsNullOrEmpty(payload.TargetDatabaseName)
            && !DatabaseNameValidator.IsValid(payload.TargetDatabaseName, out var targetReason))
        {
            return $"Имя целевой БД не прошло валидацию: {targetReason}.";
        }

        return null;
    }

    internal IUploadService ResolveUploader(RestoreTaskPayload payload)
    {
        var storageName = payload.StorageName;

        if (string.IsNullOrWhiteSpace(storageName))
        {
            var dbConfig = _databases.FirstOrDefault(
                d => string.Equals(d.Database, payload.SourceDatabaseName, StringComparison.Ordinal));

            if (dbConfig is null)
            {
                throw new InvalidOperationException(
                    $"БД '{payload.SourceDatabaseName}' не найдена в конфиге агента, а дашборд не передал StorageName. " +
                    "Добавьте БД в конфиг либо обновите дашборд, чтобы он передавал имя хранилища.");
            }

            storageName = dbConfig.StorageName;
        }

        return _uploadFactory.GetService(storageName);
    }

    internal static PatchAgentTaskDto CombineResults(DatabaseRestoreResult db, FileRestoreResult files)
    {
        var databaseStatus = db.IsSuccess ? RestoreDatabaseStatus.Success : RestoreDatabaseStatus.Failed;
        var filesStatus = files.Status;

        AgentTaskStatus overallStatus;
        if (!db.IsSuccess)
            overallStatus = AgentTaskStatus.Failed;
        else if (filesStatus is RestoreFilesStatus.Failed or RestoreFilesStatus.Partial)
            overallStatus = AgentTaskStatus.Partial;
        else
            overallStatus = AgentTaskStatus.Success;

        string? errorMessage;
        if (db.ErrorMessage is not null && files.ErrorMessage is not null)
            errorMessage = $"{db.ErrorMessage}\n\n{files.ErrorMessage}";
        else
            errorMessage = db.ErrorMessage ?? files.ErrorMessage;

        return new PatchAgentTaskDto
        {
            Status = overallStatus,
            ErrorMessage = errorMessage,
            Restore = new RestoreTaskResult
            {
                DatabaseStatus = databaseStatus,
                FilesStatus = filesStatus,
                FilesRestoredCount = files.FilesRestoredCount > 0 ? files.FilesRestoredCount : null,
                FilesFailedCount = files.FilesFailedCount > 0 ? files.FilesFailedCount : null,
            },
        };
    }

    private static async Task<bool> DelayOrCancel(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
