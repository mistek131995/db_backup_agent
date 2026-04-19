using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackupsterAgent.Contracts;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace BackupsterAgent.Services.Dashboard;

public sealed class RetentionClient : IRetentionClient
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

    private readonly HttpClient _http;
    private readonly AgentSettings _settings;
    private readonly IDashboardAuthGuard _authGuard;
    private readonly ILogger<RetentionClient> _logger;
    private readonly ResiliencePipeline _writePipeline;

    public RetentionClient(
        HttpClient http,
        IOptions<AgentSettings> settings,
        IDashboardAuthGuard authGuard,
        ILogger<RetentionClient> logger)
    {
        _http = http;
        _settings = settings.Value;
        _authGuard = authGuard;
        _logger = logger;
        _writePipeline = BuildRetryPipeline();
    }

    public async Task<IReadOnlyList<ExpiredBackupRecordDto>> GetExpiredAsync(int limit, CancellationToken ct)
    {
        if (!IsConfigured()) return Array.Empty<ExpiredBackupRecordDto>();

        var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/backup-records/expired?limit={limit}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Agent-Token", _settings.Token);

        using var response = await _http.SendAsync(request, ct);
        ThrowIfUnauthorized(response, nameof(GetExpiredAsync));
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<List<ExpiredBackupRecordDto>>(JsonOptions, ct);
        return body ?? new List<ExpiredBackupRecordDto>();
    }

    public async Task DeleteAsync(Guid recordId, CancellationToken ct)
    {
        if (!IsConfigured()) return;

        var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/backup-records/{recordId}";

        await _writePipeline.ExecuteAsync(async innerCt =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            request.Headers.Add("X-Agent-Token", _settings.Token);

            using var response = await _http.SendAsync(request, innerCt);
            ThrowIfUnauthorized(response, nameof(DeleteAsync));
            response.EnsureSuccessStatusCode();
        }, ct);
    }

    public async Task MarkStorageUnreachableAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (!IsConfigured()) return;
        if (ids.Count == 0) return;

        var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/backup-records/mark-unreachable";
        var dto = new MarkStorageUnreachableDto { Ids = ids };

        await _writePipeline.ExecuteAsync(async innerCt =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("X-Agent-Token", _settings.Token);
            request.Content = JsonContent.Create(dto, options: JsonOptions);

            using var response = await _http.SendAsync(request, innerCt);
            ThrowIfUnauthorized(response, nameof(MarkStorageUnreachableAsync));
            response.EnsureSuccessStatusCode();
        }, ct);

        _logger.LogInformation(
            "RetentionClient: marked {Count} record(s) as storage-unreachable", ids.Count);
    }

    private bool IsConfigured()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Token) && !string.IsNullOrWhiteSpace(_settings.DashboardUrl))
            return true;

        _logger.LogWarning(
            "RetentionClient: AgentSettings.Token or DashboardUrl is not configured. Retention sweep is disabled.");
        return false;
    }

    private void ThrowIfUnauthorized(HttpResponseMessage response, string channel)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized) return;
        _authGuard.OnUnauthorized($"{nameof(RetentionClient)}.{channel}", _logger);
        throw new DashboardUnauthorizedException($"{nameof(RetentionClient)}.{channel}");
    }

    private ResiliencePipeline BuildRetryPipeline() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is not null and not DashboardUnauthorizedException),
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
                        "RetentionClient: retry {Attempt}/3. Error: {Message}",
                        args.AttemptNumber + 1, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
}
