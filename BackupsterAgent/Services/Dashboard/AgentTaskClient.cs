using System.Net;
using System.Net.Http.Json;
using BackupsterAgent.Contracts;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;
using Polly;

namespace BackupsterAgent.Services.Dashboard;

public sealed class AgentTaskClient : DashboardClientBase, IAgentTaskClient
{
    private readonly HttpClient _http;
    private readonly ILogger<AgentTaskClient> _logger;
    private readonly ResiliencePipeline _patchPipeline;

    public AgentTaskClient(
        HttpClient http,
        IOptions<AgentSettings> settings,
        IDashboardAuthGuard authGuard,
        ILogger<AgentTaskClient> logger)
        : base(settings.Value, authGuard)
    {
        _http = http;
        _logger = logger;
        _patchPipeline = BuildRetryPipeline(nameof(AgentTaskClient), logger);
    }

    public async Task<AgentTaskForAgentDto?> FetchTaskAsync(CancellationToken ct)
    {
        if (!IsConfigured(_logger, nameof(AgentTaskClient))) return null;

        var url = $"{Settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/task";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Agent-Token", Settings.Token);

        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;

        ThrowIfUnauthorized(response, $"{nameof(AgentTaskClient)}.{nameof(FetchTaskAsync)}", _logger);
        response.EnsureSuccessStatusCode();

        var task = await response.Content.ReadFromJsonAsync<AgentTaskForAgentDto>(JsonOptions, ct);
        if (task is null)
        {
            _logger.LogWarning("AgentTaskClient: server returned empty body for non-204 response");
            return null;
        }

        _logger.LogInformation(
            "AgentTaskClient: received task {TaskId} (type '{Type}')", task.Id, task.Type);

        return task;
    }

    public async Task PatchTaskAsync(Guid taskId, PatchAgentTaskDto patch, CancellationToken ct)
    {
        if (!IsConfigured(_logger, nameof(AgentTaskClient))) return;

        var url = $"{Settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/task/{taskId}";

        await _patchPipeline.ExecuteAsync(async innerCt =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, url);
            request.Headers.Add("X-Agent-Token", Settings.Token);
            request.Content = JsonContent.Create(patch, options: JsonOptions);

            using var response = await _http.SendAsync(request, innerCt);
            ThrowIfUnauthorized(response, $"{nameof(AgentTaskClient)}.{nameof(PatchTaskAsync)}", _logger);
            response.EnsureSuccessStatusCode();
        }, ct);

        _logger.LogInformation(
            "AgentTaskClient: patched task {TaskId} with status '{Status}'", taskId, patch.Status);
    }

    public async Task ReportProgressAsync(Guid taskId, AgentTaskProgressDto progress, CancellationToken ct)
    {
        if (!IsConfigured(_logger, nameof(AgentTaskClient))) return;

        var url = $"{Settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/task/{taskId}/progress";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-Agent-Token", Settings.Token);
        request.Content = JsonContent.Create(progress, options: JsonOptions);

        using var response = await _http.SendAsync(request, timeoutCts.Token);
        ThrowIfUnauthorized(response, $"{nameof(AgentTaskClient)}.{nameof(ReportProgressAsync)}", _logger);
        response.EnsureSuccessStatusCode();
    }
}
