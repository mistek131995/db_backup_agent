using System.Net.Http.Json;
using BackupsterAgent.Contracts;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;
using Polly;

namespace BackupsterAgent.Services.Dashboard;

public sealed class ConnectionSyncService : DashboardClientBase, IConnectionSyncService
{
    private readonly HttpClient _http;
    private readonly ConnectionResolver _connections;
    private readonly ILogger<ConnectionSyncService> _logger;
    private readonly ResiliencePipeline _pipeline;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConnectionSyncService(
        HttpClient http,
        ConnectionResolver connections,
        IOptions<AgentSettings> settings,
        IDashboardAuthGuard authGuard,
        ILogger<ConnectionSyncService> logger)
        : base(settings.Value, authGuard)
    {
        _http = http;
        _connections = connections;
        _logger = logger;
        _pipeline = BuildRetryPipeline(nameof(ConnectionSyncService), logger);
    }

    public async Task<bool> SyncAsync(CancellationToken ct = default)
    {
        if (!IsConfigured(_logger, nameof(ConnectionSyncService))) return false;

        await _gate.WaitAsync(ct);
        try
        {
            var payload = BuildPayload();
            var tokenHint = Settings.Token.Length >= 8 ? Settings.Token[..8] : Settings.Token;

            if (payload.Connections.Count == 0)
            {
                _logger.LogWarning(
                    "ConnectionSyncService: no fully configured connections to sync (all entries have empty Host). Skipping.");
                return true;
            }

            _logger.LogInformation(
                "ConnectionSyncService: syncing {Count} connection(s), token '{TokenHint}...'",
                payload.Connections.Count, tokenHint);

            try
            {
                await _pipeline.ExecuteAsync(async innerCt =>
                {
                    var url = $"{Settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/connections";

                    using var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Add("X-Agent-Token", Settings.Token);
                    request.Content = JsonContent.Create(payload, options: JsonOptions);

                    var response = await _http.SendAsync(request, innerCt);
                    ThrowIfUnauthorized(response, $"{nameof(ConnectionSyncService)}.{nameof(SyncAsync)}", _logger);
                    response.EnsureSuccessStatusCode();
                }, ct);

                _logger.LogInformation(
                    "ConnectionSyncService: sync delivered ({Count} connection(s))",
                    payload.Connections.Count);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ConnectionSyncService: all retry attempts exhausted. Sync not delivered.");
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private ConnectionSyncRequestDto BuildPayload()
    {
        var items = new List<ConnectionSyncItemDto>();

        foreach (var name in _connections.Names)
        {
            var conn = _connections.Resolve(name);

            if (string.IsNullOrWhiteSpace(conn.Host))
            {
                _logger.LogWarning(
                    "ConnectionSyncService: skipping connection '{Name}' — Host is empty.",
                    conn.Name);
                continue;
            }

            if (conn.Port <= 0 || conn.Port > 65535)
            {
                _logger.LogWarning(
                    "ConnectionSyncService: skipping connection '{Name}' — Port {Port} is out of range.",
                    conn.Name, conn.Port);
                continue;
            }

            items.Add(new ConnectionSyncItemDto
            {
                Name = conn.Name,
                DatabaseType = conn.DatabaseType.ToString(),
                Host = conn.Host,
                Port = conn.Port,
            });
        }

        return new ConnectionSyncRequestDto { Connections = items };
    }
}
