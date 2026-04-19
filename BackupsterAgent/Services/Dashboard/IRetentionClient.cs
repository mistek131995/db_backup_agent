using BackupsterAgent.Contracts;

namespace BackupsterAgent.Services.Dashboard;

public interface IRetentionClient
{
    Task<IReadOnlyList<ExpiredBackupRecordDto>> GetExpiredAsync(int limit, CancellationToken ct);

    Task DeleteAsync(Guid recordId, CancellationToken ct);

    Task MarkStorageUnreachableAsync(IReadOnlyList<Guid> ids, CancellationToken ct);
}
