namespace DbBackupAgent.Services;

public interface IConnectionSyncService
{
    Task<bool> SyncAsync(CancellationToken ct = default);
}
