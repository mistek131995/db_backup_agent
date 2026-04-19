using BackupsterAgent.Contracts;

namespace BackupsterAgent.Services.Dashboard;

public interface IRestoreTaskClient
{
    Task<RestoreTaskForAgentDto?> FetchTaskAsync(CancellationToken ct);

    Task PatchTaskAsync(Guid taskId, PatchRestoreTaskDto patch, CancellationToken ct);

    Task ReportProgressAsync(Guid taskId, RestoreProgressDto progress, CancellationToken ct);
}
