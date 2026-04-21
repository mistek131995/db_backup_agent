using System.Net.Http.Json;
using BackupsterAgent.Contracts;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;
using Polly;

namespace BackupsterAgent.Services.Dashboard;

public sealed class BackupRecordClient : DashboardClientBase, IBackupRecordClient
{
    private readonly HttpClient _http;
    private readonly ILogger<BackupRecordClient> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    public BackupRecordClient(
        HttpClient http,
        IOptions<AgentSettings> settings,
        IDashboardAuthGuard authGuard,
        ILogger<BackupRecordClient> logger)
        : base(settings.Value, authGuard)
    {
        _http = http;
        _logger = logger;
        _retryPipeline = BuildRetryPipeline(nameof(BackupRecordClient), logger);
    }

    public async Task<OpenRecordResult> OpenAsync(OpenBackupRecordDto dto, CancellationToken ct)
    {
        if (!IsConfigured(_logger, nameof(BackupRecordClient)))
            return new OpenRecordResult(DashboardAvailability.PermanentSkip);

        var url = $"{Settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/backup-record";

        try
        {
            var response = await _retryPipeline.ExecuteAsync(async innerCt =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("X-Agent-Token", Settings.Token);
                request.Content = JsonContent.Create(dto, options: JsonOptions);

                var resp = await _http.SendAsync(request, innerCt);
                ThrowIfUnauthorized(resp, $"{nameof(BackupRecordClient)}.{nameof(OpenAsync)}", _logger);
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

                var body = await response.Content.ReadFromJsonAsync<OpenBackupRecordResponseDto>(JsonOptions, ct);

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
        if (!IsConfigured(_logger, nameof(BackupRecordClient))) return;

        var url = $"{Settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/backup-record/{backupRecordId}/progress";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-Agent-Token", Settings.Token);
        request.Content = JsonContent.Create(progress, options: JsonOptions);

        using var response = await _http.SendAsync(request, timeoutCts.Token);
        ThrowIfUnauthorized(response, $"{nameof(BackupRecordClient)}.{nameof(ReportProgressAsync)}", _logger);
        response.EnsureSuccessStatusCode();
    }

    public async Task<FinalizeRecordResult> FinalizeAsync(
        Guid backupRecordId, FinalizeBackupRecordDto dto, CancellationToken ct)
    {
        if (!IsConfigured(_logger, nameof(BackupRecordClient)))
            return new FinalizeRecordResult(DashboardAvailability.PermanentSkip);

        var url = $"{Settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/backup-record/{backupRecordId}";

        try
        {
            var availability = DashboardAvailability.Ok;

            await _retryPipeline.ExecuteAsync(async innerCt =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Patch, url);
                request.Headers.Add("X-Agent-Token", Settings.Token);
                request.Content = JsonContent.Create(dto, options: JsonOptions);

                using var response = await _http.SendAsync(request, innerCt);
                ThrowIfUnauthorized(response, $"{nameof(BackupRecordClient)}.{nameof(FinalizeAsync)}", _logger);

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

    private void LogNonOk(string channel, string subject, DashboardAvailability availability, System.Net.HttpStatusCode? status)
    {
        _logger.LogWarning(
            "BackupRecordClient.{Channel}: '{Subject}' classified as {Availability}{Status}",
            channel, subject, availability,
            status.HasValue ? $" (HTTP {(int)status.Value})" : string.Empty);
    }
}
