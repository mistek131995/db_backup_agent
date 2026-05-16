using System.Net.Http.Json;
using BackupsterAgent.Configuration;
using BackupsterAgent.Contracts;
using BackupsterAgent.Services.Common.Resolvers;
using Microsoft.Extensions.Options;
using Polly;

namespace BackupsterAgent.Services.Dashboard.Sync;

public sealed class StorageSyncService : DashboardClientBase, IStorageSyncService
{
    private readonly HttpClient _http;
    private readonly StorageResolver _storages;
    private readonly ILogger<StorageSyncService> _logger;
    private readonly ResiliencePipeline _pipeline;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StorageSyncService(
        HttpClient http,
        StorageResolver storages,
        IOptions<AgentSettings> settings,
        IDashboardAuthGuard authGuard,
        ILogger<StorageSyncService> logger)
        : base(settings.Value, authGuard)
    {
        _http = http;
        _storages = storages;
        _logger = logger;
        _pipeline = BuildRetryPipeline(nameof(StorageSyncService), logger);
    }

    public async Task<bool> SyncAsync(CancellationToken ct = default)
    {
        if (!IsConfigured(_logger, nameof(StorageSyncService))) return false;

        await _gate.WaitAsync(ct);
        try
        {
            var payload = BuildPayload();

            if (payload.Storages.Count == 0)
            {
                _logger.LogWarning(
                    "StorageSyncService: no storages to sync (all entries have empty Name). Skipping.");
                return true;
            }

            _logger.LogInformation(
                "StorageSyncService: syncing {Count} storage(s)",
                payload.Storages.Count);

            try
            {
                await _pipeline.ExecuteAsync(async innerCt =>
                {
                    var url = $"{Settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/storages";

                    using var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Add("X-Agent-Token", Settings.Token);
                    request.Content = JsonContent.Create(payload, options: JsonOptions);

                    var response = await _http.SendAsync(request, innerCt);
                    ThrowIfUnauthorized(response, $"{nameof(StorageSyncService)}.{nameof(SyncAsync)}", _logger);
                    response.EnsureSuccessStatusCode();
                }, ct);

                _logger.LogInformation(
                    "StorageSyncService: sync delivered ({Count} storage(s))",
                    payload.Storages.Count);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "StorageSyncService: all retry attempts exhausted. Sync not delivered.");
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private StorageSyncRequestDto BuildPayload()
    {
        var items = new List<StorageSyncItemDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in _storages.Names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                _logger.LogWarning("StorageSyncService: skipping storage with empty Name.");
                continue;
            }

            if (!seen.Add(name))
            {
                _logger.LogWarning(
                    "StorageSyncService: skipping duplicate storage name '{Name}'.", name);
                continue;
            }

            var storage = _storages.Resolve(name);

            items.Add(new StorageSyncItemDto
            {
                Name = storage.Name,
                Provider = storage.Provider.ToString(),
            });
        }

        return new StorageSyncRequestDto { Storages = items };
    }
}
