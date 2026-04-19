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

public sealed class RestoreTaskClient : IRestoreTaskClient
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
    private readonly ILogger<RestoreTaskClient> _logger;
    private readonly ResiliencePipeline _patchPipeline;

    public RestoreTaskClient(
        HttpClient http,
        IOptions<AgentSettings> settings,
        ILogger<RestoreTaskClient> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
        _patchPipeline = BuildPatchPipeline();
    }

    public async Task<RestoreTaskForAgentDto?> FetchTaskAsync(CancellationToken ct)
    {
        if (!IsConfigured()) return null;

        var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/restore-task";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Agent-Token", _settings.Token);

        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;

        response.EnsureSuccessStatusCode();

        var task = await response.Content.ReadFromJsonAsync<RestoreTaskForAgentDto>(ct);
        if (task is null)
        {
            _logger.LogWarning("RestoreTaskClient: server returned empty body for non-204 response");
            return null;
        }

        _logger.LogInformation(
            "RestoreTaskClient: received task {TaskId} (source '{Source}', target '{Target}')",
            task.TaskId, task.SourceDatabaseName, task.TargetDatabaseName ?? task.SourceDatabaseName);

        return task;
    }

    public async Task PatchTaskAsync(Guid taskId, PatchRestoreTaskDto patch, CancellationToken ct)
    {
        if (!IsConfigured()) return;

        var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/restore-task/{taskId}";

        await _patchPipeline.ExecuteAsync(async innerCt =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, url);
            request.Headers.Add("X-Agent-Token", _settings.Token);
            request.Content = JsonContent.Create(patch, options: JsonOptions);

            using var response = await _http.SendAsync(request, innerCt);
            response.EnsureSuccessStatusCode();
        }, ct);

        _logger.LogInformation(
            "RestoreTaskClient: patched task {TaskId} with status '{Status}'",
            taskId, patch.Status);
    }

    public async Task ReportProgressAsync(Guid taskId, RestoreProgressDto progress, CancellationToken ct)
    {
        if (!IsConfigured()) return;

        var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/restore-task/{taskId}/progress";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-Agent-Token", _settings.Token);
        request.Content = JsonContent.Create(progress, options: JsonOptions);

        using var response = await _http.SendAsync(request, timeoutCts.Token);
        response.EnsureSuccessStatusCode();
    }

    private bool IsConfigured()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Token) && !string.IsNullOrWhiteSpace(_settings.DashboardUrl))
            return true;

        _logger.LogWarning(
            "RestoreTaskClient: AgentSettings.Token or DashboardUrl is not configured. Restore polling is disabled.");
        return false;
    }

    private ResiliencePipeline BuildPatchPipeline() =>
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
                        "RestoreTaskClient: PATCH retry {Attempt}/3. Error: {Message}",
                        args.AttemptNumber + 1, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
}
