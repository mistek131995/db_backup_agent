using DbBackupAgent.Contracts;

namespace DbBackupAgent.Services;

public interface IRestoreTaskClient
{
    Task<RestoreTaskForAgentDto?> FetchTaskAsync(CancellationToken ct);

    Task PatchTaskAsync(Guid taskId, PatchRestoreTaskDto patch, CancellationToken ct);
}
