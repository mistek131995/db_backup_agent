using BackupsterAgent.Configuration;
using BackupsterAgent.Services.Common.Resolvers;
using BackupsterAgent.Services.Dashboard.Sync;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Workers;

public sealed class TopologySyncWorker : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    private readonly IConnectionSyncService _connectionSync;
    private readonly IDatabaseSyncService _databaseSync;
    private readonly IFileSetSyncService _fileSetSync;
    private readonly IStorageSyncService _storageSync;
    private readonly ConnectionResolver _connections;
    private readonly StorageResolver _storages;
    private readonly List<DatabaseConfig> _databases;
    private readonly List<FileSetConfig> _fileSets;
    private readonly ILogger<TopologySyncWorker> _logger;

    public TopologySyncWorker(
        IConnectionSyncService connectionSync,
        IDatabaseSyncService databaseSync,
        IFileSetSyncService fileSetSync,
        IStorageSyncService storageSync,
        ConnectionResolver connections,
        StorageResolver storages,
        IOptions<List<DatabaseConfig>> databases,
        IOptions<List<FileSetConfig>> fileSets,
        ILogger<TopologySyncWorker> logger)
    {
        _connectionSync = connectionSync;
        _databaseSync = databaseSync;
        _fileSetSync = fileSetSync;
        _storageSync = storageSync;
        _connections = connections;
        _storages = storages;
        _databases = databases.Value;
        _fileSets = fileSets.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TopologySyncWorker started. Slots: connections={ConnCount}, databases={DbCount}, filesets={FsCount}, storages={StCount}",
            _connections.Names.Count, _databases.Count, _fileSets.Count, _storages.Names.Count);

        var slots = new[]
        {
            RunSlotAsync("connections", _connections.Names.Count > 0, _connectionSync.SyncAsync, stoppingToken),
            RunSlotAsync("databases", _databases.Count > 0, _databaseSync.SyncAsync, stoppingToken),
            RunSlotAsync("filesets", _fileSets.Count > 0, _fileSetSync.SyncAsync, stoppingToken),
            RunSlotAsync("storages", _storages.Names.Count > 0, _storageSync.SyncAsync, stoppingToken),
        };

        await Task.WhenAll(slots);

        _logger.LogInformation("TopologySyncWorker: all slots finished, worker stopping.");
    }

    private async Task RunSlotAsync(
        string slot,
        bool hasItems,
        Func<CancellationToken, Task<bool>> sync,
        CancellationToken stoppingToken)
    {
        if (!hasItems)
        {
            _logger.LogInformation(
                "TopologySyncWorker[{Slot}]: nothing to sync, slot finished immediately.", slot);
            return;
        }

        var delay = InitialDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ok = await sync(stoppingToken);
                if (ok)
                {
                    _logger.LogInformation(
                        "TopologySyncWorker[{Slot}]: initial sync succeeded, slot finished.", slot);
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "TopologySyncWorker[{Slot}]: unexpected error during sync attempt.", slot);
            }

            _logger.LogWarning(
                "TopologySyncWorker[{Slot}]: sync not delivered, retrying in {DelaySec}s.",
                slot, (int)delay.TotalSeconds);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxDelay.TotalSeconds));
        }
    }
}
