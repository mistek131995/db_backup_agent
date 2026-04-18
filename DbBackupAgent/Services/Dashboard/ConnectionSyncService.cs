using System.Net.Http.Json;
using DbBackupAgent.Contracts;
using DbBackupAgent.Services.Common;
using DbBackupAgent.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace DbBackupAgent.Services;

public sealed class ConnectionSyncService : IConnectionSyncService
{
    private readonly HttpClient _http;
    private readonly ConnectionResolver _connections;
    private readonly AgentSettings _settings;
    private readonly ILogger<ConnectionSyncService> _logger;
    private readonly ResiliencePipeline _pipeline;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    public ConnectionSyncService(
        HttpClient http,
        ConnectionResolver connections,
        IOptions<AgentSettings> settings,
        ILogger<ConnectionSyncService> logger)
    {
        _http = http;
        _connections = connections;
        _settings = settings.Value;
        _logger = logger;
        _pipeline = BuildPipeline();
    }

    public async Task<bool> SyncAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.Token) || string.IsNullOrWhiteSpace(_settings.DashboardUrl))
        {
            _logger.LogWarning(
                "ConnectionSyncService: skipping sync — AgentSettings.Token or DashboardUrl is not configured.");
            return false;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var payload = BuildPayload();
            var tokenHint = _settings.Token.Length >= 8 ? _settings.Token[..8] : _settings.Token;

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
                    var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/connections";

                    using var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Add("X-Agent-Token", _settings.Token);
                    request.Content = JsonContent.Create(payload);

                    var response = await _http.SendAsync(request, innerCt);
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

    private ResiliencePipeline BuildPipeline() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                DelayGenerator = args =>
                {
                    var delay = args.AttemptNumber < RetryDelays.Length
                        ? RetryDelays[args.AttemptNumber]
                        : RetryDelays[^1];
                    return ValueTask.FromResult<TimeSpan?>(delay);
                },
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "ConnectionSyncService: retry attempt {Attempt}/3, delay {DelaySeconds}s. Error: {Message}",
                        args.AttemptNumber + 1,
                        RetryDelays[Math.Min(args.AttemptNumber, RetryDelays.Length - 1)].TotalSeconds,
                        args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
}
