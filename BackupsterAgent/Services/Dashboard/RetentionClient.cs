using System.Net.Http.Json;
using BackupsterAgent.Contracts;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;
using Polly;

namespace BackupsterAgent.Services.Dashboard;

public sealed class RetentionClient : DashboardClientBase, IRetentionClient
{
    private readonly HttpClient _http;
    private readonly ILogger<RetentionClient> _logger;
    private readonly ResiliencePipeline _writePipeline;

    public RetentionClient(
        HttpClient http,
        IOptions<AgentSettings> settings,
        IDashboardAuthGuard authGuard,
        ILogger<RetentionClient> logger)
        : base(settings.Value, authGuard)
    {
        _http = http;
        _logger = logger;
        _writePipeline = BuildRetryPipeline(nameof(RetentionClient), logger);
    }

    public async Task<IReadOnlyList<ExpiredBackupRecordDto>> GetExpiredAsync(int limit, CancellationToken ct)
    {
        if (!IsConfigured(_logger, nameof(RetentionClient))) return Array.Empty<ExpiredBackupRecordDto>();

        var url = $"{Settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/backup-records/expired?limit={limit}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Agent-Token", Settings.Token);

        using var response = await _http.SendAsync(request, ct);
        ThrowIfUnauthorized(response, $"{nameof(RetentionClient)}.{nameof(GetExpiredAsync)}", _logger);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<List<ExpiredBackupRecordDto>>(JsonOptions, ct);
        return body ?? new List<ExpiredBackupRecordDto>();
    }

    public async Task DeleteAsync(Guid recordId, CancellationToken ct)
    {
        if (!IsConfigured(_logger, nameof(RetentionClient))) return;

        var url = $"{Settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/backup-records/{recordId}";

        await _writePipeline.ExecuteAsync(async innerCt =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            request.Headers.Add("X-Agent-Token", Settings.Token);

            using var response = await _http.SendAsync(request, innerCt);
            ThrowIfUnauthorized(response, $"{nameof(RetentionClient)}.{nameof(DeleteAsync)}", _logger);
            response.EnsureSuccessStatusCode();
        }, ct);
    }

    public async Task MarkStorageUnreachableAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (!IsConfigured(_logger, nameof(RetentionClient))) return;
        if (ids.Count == 0) return;

        var url = $"{Settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/backup-records/mark-unreachable";
        var dto = new MarkStorageUnreachableDto { Ids = ids };

        await _writePipeline.ExecuteAsync(async innerCt =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("X-Agent-Token", Settings.Token);
            request.Content = JsonContent.Create(dto, options: JsonOptions);

            using var response = await _http.SendAsync(request, innerCt);
            ThrowIfUnauthorized(response, $"{nameof(RetentionClient)}.{nameof(MarkStorageUnreachableAsync)}", _logger);
            response.EnsureSuccessStatusCode();
        }, ct);

        _logger.LogInformation(
            "RetentionClient: marked {Count} record(s) as storage-unreachable", ids.Count);
    }
}
