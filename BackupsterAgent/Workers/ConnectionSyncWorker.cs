using BackupsterAgent.Services;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Dashboard;
using Microsoft.Extensions.Logging;

namespace BackupsterAgent.Workers;

public sealed class ConnectionSyncWorker : BackgroundService
{
    private readonly IConnectionSyncService _sync;
    private readonly ConnectionResolver _connections;
    private readonly ILogger<ConnectionSyncWorker> _logger;

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    public ConnectionSyncWorker(
        IConnectionSyncService sync,
        ConnectionResolver connections,
        ILogger<ConnectionSyncWorker> logger)
    {
        _sync = sync;
        _connections = connections;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ConnectionSyncWorker started. Connections to sync: {Count}",
            _connections.Names.Count);

        var delay = InitialDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ok = await _sync.SyncAsync(stoppingToken);
                if (ok)
                {
                    _logger.LogInformation("ConnectionSyncWorker: initial sync succeeded, worker stopping.");
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConnectionSyncWorker: unexpected error during sync attempt");
            }

            _logger.LogWarning(
                "ConnectionSyncWorker: sync not delivered, retrying in {DelaySec}s",
                (int)delay.TotalSeconds);

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
