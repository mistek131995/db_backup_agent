namespace BackupsterAgent.Services.Dashboard;

public interface IConnectionSyncService
{
    Task<bool> SyncAsync(CancellationToken ct = default);
}
