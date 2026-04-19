using DbBackupAgent.Configuration;
using DbBackupAgent.Contracts;
using DbBackupAgent.Domain;
using DbBackupAgent.Enums;
using DbBackupAgent.Services;
using DbBackupAgent.Services.Common;
using DbBackupAgent.Services.Dashboard;
using DbBackupAgent.Services.Restore;
using DbBackupAgent.Services.Upload;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbBackupAgent.Workers;

public sealed class RestoreTaskPollingService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly IRestoreTaskClient _client;
    private readonly DatabaseRestoreService _databaseRestore;
    private readonly FileRestoreService _fileRestore;
    private readonly IAgentActivityLock _activityLock;
    private readonly IProgressReporterFactory _reporterFactory;
    private readonly IUploadServiceFactory _uploadFactory;
    private readonly List<DatabaseConfig> _databases;
    private readonly ILogger<RestoreTaskPollingService> _logger;

    public RestoreTaskPollingService(
        IRestoreTaskClient client,
        DatabaseRestoreService databaseRestore,
        FileRestoreService fileRestore,
        IAgentActivityLock activityLock,
        IProgressReporterFactory reporterFactory,
        IUploadServiceFactory uploadFactory,
        IOptions<List<DatabaseConfig>> databases,
        ILogger<RestoreTaskPollingService> logger)
    {
        _client = client;
        _databaseRestore = databaseRestore;
        _fileRestore = fileRestore;
        _activityLock = activityLock;
        _reporterFactory = reporterFactory;
        _uploadFactory = uploadFactory;
        _databases = databases.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "RestoreTaskPollingService started. Poll interval: {PollSec}s, initial backoff: {BackoffSec}s, max backoff: {MaxSec}s",
            PollInterval.TotalSeconds, InitialBackoff.TotalSeconds, MaxBackoff.TotalSeconds);

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

                using (await _activityLock.AcquireAsync($"restore:{task.TaskId}", stoppingToken))
                {
                    var patch = await ExecuteTaskAsync(task, stoppingToken);
                    try
                    {
                        await _client.PatchTaskAsync(task.TaskId, patch, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "RestoreTaskPollingService: failed to PATCH final status for task {TaskId}. " +
                            "Task will be marked in_progress until sweeper picks it up.", task.TaskId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "RestoreTaskPollingService: polling error, backing off {BackoffSec}s",
                    backoff.TotalSeconds);

                if (!await DelayOrCancel(backoff, stoppingToken)) break;

                var next = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
                backoff = next;
            }
        }

        _logger.LogInformation("RestoreTaskPollingService stopped");
    }

    private async Task<PatchRestoreTaskDto> ExecuteTaskAsync(RestoreTaskForAgentDto task, CancellationToken ct)
    {
        _logger.LogInformation(
            "RestoreTaskPollingService: executing task {TaskId} (source '{Source}')",
            task.TaskId, task.SourceDatabaseName);

        await using var reporter = _reporterFactory.CreateForRestore(task.TaskId);

        IUploadService uploader;
        try
        {
            uploader = ResolveUploader(task);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RestoreTaskPollingService: failed to resolve storage for task {TaskId}", task.TaskId);
            return new PatchRestoreTaskDto
            {
                Status = RestoreTaskStatus.Failed,
                DatabaseStatus = RestoreDatabaseStatus.Failed,
                ErrorMessage = ex.Message,
            };
        }

        var dbResult = await _databaseRestore.RunAsync(task, uploader, reporter, ct);

        var fileResult = task.ManifestKey is null
            ? FileRestoreResult.Skipped()
            : await _fileRestore.RunAsync(task.ManifestKey, task.TargetFileRoot, uploader, reporter, ct);

        return CombineResults(dbResult, fileResult);
    }

    internal IUploadService ResolveUploader(RestoreTaskForAgentDto task)
    {
        var storageName = task.StorageName;

        if (string.IsNullOrWhiteSpace(storageName))
        {
            var dbConfig = _databases.FirstOrDefault(
                d => string.Equals(d.Database, task.SourceDatabaseName, StringComparison.Ordinal));

            if (dbConfig is null)
            {
                throw new InvalidOperationException(
                    $"БД '{task.SourceDatabaseName}' не найдена в конфиге агента, а дашборд не передал StorageName. " +
                    "Добавьте БД в конфиг либо обновите дашборд, чтобы он передавал имя хранилища.");
            }

            storageName = dbConfig.StorageName;
        }

        return _uploadFactory.GetService(storageName);
    }

    internal static PatchRestoreTaskDto CombineResults(DatabaseRestoreResult db, FileRestoreResult files)
    {
        var databaseStatus = db.IsSuccess ? RestoreDatabaseStatus.Success : RestoreDatabaseStatus.Failed;
        var filesStatus = files.Status;

        RestoreTaskStatus overallStatus;
        if (!db.IsSuccess)
            overallStatus = RestoreTaskStatus.Failed;
        else if (filesStatus is RestoreFilesStatus.Failed or RestoreFilesStatus.Partial)
            overallStatus = RestoreTaskStatus.Partial;
        else
            overallStatus = RestoreTaskStatus.Success;

        string? errorMessage;
        if (db.ErrorMessage is not null && files.ErrorMessage is not null)
            errorMessage = $"{db.ErrorMessage}\n\n{files.ErrorMessage}";
        else
            errorMessage = db.ErrorMessage ?? files.ErrorMessage;

        return new PatchRestoreTaskDto
        {
            Status = overallStatus,
            DatabaseStatus = databaseStatus,
            FilesStatus = filesStatus,
            ErrorMessage = errorMessage,
            FilesRestoredCount = files.FilesRestoredCount > 0 ? files.FilesRestoredCount : null,
            FilesFailedCount = files.FilesFailedCount > 0 ? files.FilesFailedCount : null,
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
