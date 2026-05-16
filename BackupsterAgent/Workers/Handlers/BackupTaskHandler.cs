using BackupsterAgent.Configuration;
using BackupsterAgent.Contracts;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Common.Resolvers;
using BackupsterAgent.Services.Common.State;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Workers.Handlers;

public sealed class BackupTaskHandler : IAgentTaskHandler
{
    private readonly BackupJob _backupJob;
    private readonly IBackupRunTracker _runTracker;
    private readonly StorageResolver _storages;
    private readonly List<DatabaseConfig> _databases;
    private readonly ILogger<BackupTaskHandler> _logger;

    public BackupTaskHandler(
        BackupJob backupJob,
        IBackupRunTracker runTracker,
        StorageResolver storages,
        IOptions<List<DatabaseConfig>> databases,
        ILogger<BackupTaskHandler> logger)
    {
        _backupJob = backupJob;
        _runTracker = runTracker;
        _storages = storages;
        _databases = databases.Value;
        _logger = logger;
    }

    public bool CanHandle(AgentTaskForAgentDto task) =>
        task.Type == AgentTaskType.Backup
        && string.IsNullOrWhiteSpace(task.Backup?.FileSetName);

    public async Task<PatchAgentTaskDto> HandleAsync(AgentTaskForAgentDto task, CancellationToken ct)
    {
        if (task.Backup is null)
        {
            _logger.LogWarning(
                "BackupTaskHandler: backup task {TaskId} has empty payload.", task.Id);
            return new PatchAgentTaskDto
            {
                Status = AgentTaskStatus.Failed,
                ErrorMessage = "Сервер не передал тело backup-задачи.",
            };
        }

        if (string.IsNullOrWhiteSpace(task.Backup.DatabaseName))
        {
            _logger.LogWarning(
                "BackupTaskHandler: backup task {TaskId} has no DatabaseName.", task.Id);
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
                "BackupTaskHandler: backup task {TaskId} references unknown database '{Database}'",
                task.Id, databaseName);
            return new PatchAgentTaskDto
            {
                Status = AgentTaskStatus.Failed,
                ErrorMessage = $"БД '{databaseName}' не найдена в конфиге агента.",
            };
        }

        var mode = task.Backup.BackupMode;

        if (!_storages.TryResolve(config.StorageName, out var storage))
        {
            _logger.LogWarning(
                "BackupTaskHandler: backup task {TaskId} — storage '{Storage}' for database '{Database}' is not configured.",
                task.Id, config.StorageName, databaseName);
            return new PatchAgentTaskDto
            {
                Status = AgentTaskStatus.Failed,
                ErrorMessage = $"Хранилище '{config.StorageName}' не настроено на агенте.",
            };
        }

        _logger.LogInformation(
            "BackupTaskHandler: executing backup task {TaskId} for database '{Database}' (mode={Mode}, storage={Storage})",
            task.Id, databaseName, mode, storage.Name);

        BackupResult result;
        try
        {
            result = await _backupJob.RunAsync(config, storage, mode, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "BackupTaskHandler: backup task {TaskId} threw", task.Id);
            return new PatchAgentTaskDto
            {
                Status = AgentTaskStatus.Failed,
                ErrorMessage = $"Неожиданная ошибка бэкапа: {ex.Message}",
            };
        }

        _runTracker.RecordRun(
            IBackupRunTracker.DatabaseKey(databaseName, mode, config.StorageName),
            DateTime.UtcNow);

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
}
