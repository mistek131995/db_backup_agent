using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackupsterAgent.Contracts;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace BackupsterAgent.Services.Dashboard;

public sealed class BackupRecordClient : IBackupRecordClient
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
    private readonly ILogger<BackupRecordClient> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    public BackupRecordClient(
        HttpClient http,
        IOptions<AgentSettings> settings,
        ILogger<BackupRecordClient> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
        _retryPipeline = BuildRetryPipeline();
    }

    public async Task<Guid?> OpenAsync(OpenBackupRecordDto dto, CancellationToken ct)
    {
        if (!IsConfigured()) return null;

        var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/backup-record";

        try
        {
            var response = await _retryPipeline.ExecuteAsync(async innerCt =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("X-Agent-Token", _settings.Token);
                request.Content = JsonContent.Create(dto, options: JsonOptions);

                var resp = await _http.SendAsync(request, innerCt);
                resp.EnsureSuccessStatusCode();
                return resp;
            }, ct);

            using (response)
            {
                var body = await response.Content.ReadFromJsonAsync<OpenBackupRecordResponseDto>(
                    JsonOptions, ct);

                if (body is null || body.Id == Guid.Empty)
                {
                    _logger.LogWarning(
                        "BackupRecordClient: server returned empty body for '{Database}'", dto.DatabaseName);
                    return null;
                }

                _logger.LogInformation(
                    "BackupRecordClient: opened record {Id} for '{Database}'", body.Id, dto.DatabaseName);
                return body.Id;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "BackupRecordClient: failed to open record for '{Database}'. Backup for this run is skipped.",
                dto.DatabaseName);
            return null;
        }
    }

    public async Task ReportProgressAsync(Guid backupRecordId, BackupProgressDto progress, CancellationToken ct)
    {
        if (!IsConfigured()) return;

        var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/backup-record/{backupRecordId}/progress";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-Agent-Token", _settings.Token);
        request.Content = JsonContent.Create(progress, options: JsonOptions);

        using var response = await _http.SendAsync(request, timeoutCts.Token);
        response.EnsureSuccessStatusCode();
    }

    public async Task FinalizeAsync(Guid backupRecordId, FinalizeBackupRecordDto dto, CancellationToken ct)
    {
        if (!IsConfigured()) return;

        var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/backup-record/{backupRecordId}";

        try
        {
            await _retryPipeline.ExecuteAsync(async innerCt =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Patch, url);
                request.Headers.Add("X-Agent-Token", _settings.Token);
                request.Content = JsonContent.Create(dto, options: JsonOptions);

                using var response = await _http.SendAsync(request, innerCt);
                response.EnsureSuccessStatusCode();
            }, ct);

            _logger.LogInformation(
                "BackupRecordClient: finalized record {Id} with status '{Status}'",
                backupRecordId, dto.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "BackupRecordClient: all retry attempts exhausted for record {Id}. Record remains in_progress.",
                backupRecordId);
        }
    }

    private bool IsConfigured()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Token) && !string.IsNullOrWhiteSpace(_settings.DashboardUrl))
            return true;

        _logger.LogWarning(
            "BackupRecordClient: AgentSettings.Token or DashboardUrl is not configured. Dashboard reporting is disabled.");
        return false;
    }

    private ResiliencePipeline BuildRetryPipeline() =>
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
                        "BackupRecordClient: retry {Attempt}/3. Error: {Message}",
                        args.AttemptNumber + 1, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
}
