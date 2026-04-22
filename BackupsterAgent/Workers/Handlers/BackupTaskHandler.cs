using BackupsterAgent.Configuration;
using BackupsterAgent.Contracts;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Workers.Handlers;

public sealed class BackupTaskHandler : IAgentTaskHandler
{
    private readonly BackupJob _backupJob;
    private readonly IBackupRunTracker _runTracker;
    private readonly List<DatabaseConfig> _databases;
    private readonly ILogger<BackupTaskHandler> _logger;

    public BackupTaskHandler(
        BackupJob backupJob,
        IBackupRunTracker runTracker,
        IOptions<List<DatabaseConfig>> databases,
        ILogger<BackupTaskHandler> logger)
    {
        _backupJob = backupJob;
        _runTracker = runTracker;
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

        _logger.LogInformation(
            "BackupTaskHandler: executing backup task {TaskId} for database '{Database}' (mode={Mode})",
            task.Id, databaseName, mode);

        BackupResult result;
        try
        {
            result = await _backupJob.RunAsync(config, mode, ct);
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
}
