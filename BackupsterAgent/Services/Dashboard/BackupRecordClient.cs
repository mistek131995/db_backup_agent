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
    private readonly IDashboardAuthGuard _authGuard;
    private readonly ILogger<BackupRecordClient> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    public BackupRecordClient(
        HttpClient http,
        IOptions<AgentSettings> settings,
        IDashboardAuthGuard authGuard,
        ILogger<BackupRecordClient> logger)
    {
        _http = http;
        _settings = settings.Value;
        _authGuard = authGuard;
        _logger = logger;
        _retryPipeline = BuildRetryPipeline();
    }

    public async Task<OpenRecordResult> OpenAsync(OpenBackupRecordDto dto, CancellationToken ct)
    {
        if (!IsConfigured()) return new OpenRecordResult(DashboardAvailability.PermanentSkip);

        var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/backup-record";

        try
        {
            var response = await _retryPipeline.ExecuteAsync(async innerCt =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("X-Agent-Token", _settings.Token);
                request.Content = JsonContent.Create(dto, options: JsonOptions);

                var resp = await _http.SendAsync(request, innerCt);
                ThrowIfUnauthorized(resp, nameof(OpenAsync));
                return resp;
            }, ct);

            using (response)
            {
                var availability = DashboardAvailabilityPolicy.ClassifyResponse(response);
                if (availability != DashboardAvailability.Ok)
                {
                    LogNonOk(nameof(OpenAsync), dto.DatabaseName, availability, response.StatusCode);
                    return new OpenRecordResult(availability);
                }

                var body = await response.Content.ReadFromJsonAsync<OpenBackupRecordResponseDto>(
                    JsonOptions, ct);

                if (body is null || body.Id == Guid.Empty)
                {
                    _logger.LogWarning(
                        "BackupRecordClient: server returned empty body for '{Database}'", dto.DatabaseName);
                    return new OpenRecordResult(DashboardAvailability.PermanentSkip);
                }

                _logger.LogInformation(
                    "BackupRecordClient: opened record {Id} for '{Database}'", body.Id, dto.DatabaseName);
                return new OpenRecordResult(DashboardAvailability.Ok, body.Id);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var availability = DashboardAvailabilityPolicy.ClassifyException(ex);
            _logger.LogWarning(ex,
                "BackupRecordClient: open failed for '{Database}' — classified as {Availability}",
                dto.DatabaseName, availability);
            return new OpenRecordResult(availability);
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
        ThrowIfUnauthorized(response, nameof(ReportProgressAsync));
        response.EnsureSuccessStatusCode();
    }

    public async Task<FinalizeRecordResult> FinalizeAsync(
        Guid backupRecordId, FinalizeBackupRecordDto dto, CancellationToken ct)
    {
        if (!IsConfigured()) return new FinalizeRecordResult(DashboardAvailability.PermanentSkip);

        var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/backup-record/{backupRecordId}";

        try
        {
            var availability = DashboardAvailability.Ok;

            await _retryPipeline.ExecuteAsync(async innerCt =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Patch, url);
                request.Headers.Add("X-Agent-Token", _settings.Token);
                request.Content = JsonContent.Create(dto, options: JsonOptions);

                using var response = await _http.SendAsync(request, innerCt);
                ThrowIfUnauthorized(response, nameof(FinalizeAsync));

                availability = DashboardAvailabilityPolicy.ClassifyResponse(response);
                if (availability == DashboardAvailability.OfflineRetryable)
                    response.EnsureSuccessStatusCode();
            }, ct);

            if (availability == DashboardAvailability.Ok)
            {
                _logger.LogInformation(
                    "BackupRecordClient: finalized record {Id} with status '{Status}'",
                    backupRecordId, dto.Status);
            }
            else
            {
                LogNonOk(nameof(FinalizeAsync), backupRecordId.ToString(), availability, status: null);
            }

            return new FinalizeRecordResult(availability);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var availability = DashboardAvailabilityPolicy.ClassifyException(ex);
            _logger.LogWarning(ex,
                "BackupRecordClient: finalize failed for record {Id} — classified as {Availability}",
                backupRecordId, availability);
            return new FinalizeRecordResult(availability);
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

    private void ThrowIfUnauthorized(HttpResponseMessage response, string channel)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized) return;
        _authGuard.OnUnauthorized($"{nameof(BackupRecordClient)}.{channel}", _logger);
        throw new DashboardUnauthorizedException($"{nameof(BackupRecordClient)}.{channel}");
    }

    private void LogNonOk(string channel, string subject, DashboardAvailability availability, System.Net.HttpStatusCode? status)
    {
        _logger.LogWarning(
            "BackupRecordClient.{Channel}: '{Subject}' classified as {Availability}{Status}",
            channel, subject, availability,
            status.HasValue ? $" (HTTP {(int)status.Value})" : string.Empty);
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
                        "BackupRecordClient: retry {Attempt}/3. Error: {Message}",
                        args.AttemptNumber + 1, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
}
