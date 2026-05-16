namespace BackupsterAgent.Services.Dashboard.Sync;

public interface IStorageSyncService
{
    Task<bool> SyncAsync(CancellationToken ct = default);
}
