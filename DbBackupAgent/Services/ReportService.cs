using System.Net.Http.Json;
using DbBackupAgent.Models;
using DbBackupAgent.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace DbBackupAgent.Services;

public sealed class ReportService
{
    private readonly HttpClient _http;
    private readonly AgentSettings _settings;
    private readonly ILogger<ReportService> _logger;
    private readonly ResiliencePipeline _pipeline;

    // Fixed delays: attempt 0 → 1 s, attempt 1 → 2 s, attempt 2 → 4 s
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    public ReportService(
        HttpClient http,
        IOptions<AgentSettings> settings,
        ILogger<ReportService> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
        _pipeline = BuildPipeline();
    }

    public async Task ReportAsync(BackupReportDto dto, CancellationToken ct)
    {
        var tokenHint = _settings.Token.Length >= 8 ? _settings.Token[..8] : _settings.Token;

        _logger.LogInformation(
            "ReportService: sending report for '{Database}', status '{Status}', token '{TokenHint}...'",
            dto.DatabaseName, dto.Status, tokenHint);

        try
        {
            await _pipeline.ExecuteAsync(async innerCt =>
            {
                var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/report";

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("X-Agent-Token", _settings.Token);
                request.Content = JsonContent.Create(dto);

                var response = await _http.SendAsync(request, innerCt);
                response.EnsureSuccessStatusCode();
            }, ct);

            _logger.LogInformation(
                "ReportService: report delivered for '{Database}'", dto.DatabaseName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ReportService: all retry attempts exhausted for '{Database}'. Report not delivered.",
                dto.DatabaseName);
        }
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
                        "ReportService: retry attempt {Attempt}/3, delay {DelaySeconds}s. Error: {Message}",
                        args.AttemptNumber + 1,
                        RetryDelays[Math.Min(args.AttemptNumber, RetryDelays.Length - 1)].TotalSeconds,
                        args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
}
