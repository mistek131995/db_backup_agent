using System.Net.Http.Json;
using DbBackupAgent.Models;
using DbBackupAgent.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NCrontab;
using Polly;
using Polly.Retry;

namespace DbBackupAgent.Services;

public sealed class ScheduleService
{
    private readonly HttpClient _http;
    private readonly AgentSettings _settings;
    private readonly ILogger<ScheduleService> _logger;
    private readonly ResiliencePipeline _pipeline;

    /// <summary>How often to ask the server for a fresh schedule.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    private ScheduleDto? _cachedSchedule;
    private DateTime _lastFetchAt = DateTime.MinValue;
    private string? _lastCronExpression;

    public ScheduleService(
        HttpClient http,
        IOptions<AgentSettings> settings,
        ILogger<ScheduleService> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
        _pipeline = BuildPipeline();
    }

    /// <summary>
    /// Returns the next scheduled run time (looking back one <see cref="PollInterval"/> so a
    /// recently-past cron fire is not missed), or <c>null</c> if the schedule is inactive.
    /// Fetches a fresh schedule from the server when the poll interval elapses;
    /// falls back to the last known schedule on network errors.
    /// </summary>
    public async Task<DateTime?> GetNextRunAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastFetchAt >= PollInterval)
            await RefreshScheduleAsync(ct);

        if (_cachedSchedule is null || !_cachedSchedule.IsActive)
            return null;

        return ParseNextOccurrence(_cachedSchedule.CronExpression);
    }

    private async Task RefreshScheduleAsync(CancellationToken ct)
    {
        try
        {
            ScheduleDto? fetched = null;

            await _pipeline.ExecuteAsync(async innerCt =>
            {
                var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/schedule";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-Agent-Token", _settings.Token);

                var response = await _http.SendAsync(request, innerCt);
                response.EnsureSuccessStatusCode();
                fetched = await response.Content.ReadFromJsonAsync<ScheduleDto>(innerCt);
            }, ct);

            if (fetched is null) return;

            if (fetched.CronExpression != _lastCronExpression)
            {
                _logger.LogInformation(
                    "ScheduleService: cron expression changed. Old: '{OldCron}' → New: '{NewCron}'",
                    _lastCronExpression ?? "(none)", fetched.CronExpression);
                _lastCronExpression = fetched.CronExpression;
            }

            _cachedSchedule = fetched;
            _lastFetchAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // Back off for another full interval to avoid hammering an unavailable server.
            _lastFetchAt = DateTime.UtcNow;
            _logger.LogWarning(ex,
                "ScheduleService: failed to fetch schedule from server. Using last known schedule.");
        }
    }

    /// <summary>
    /// Gets the next cron occurrence using a one-poll-interval lookback so a fire that happened
    /// just before the worker woke up is not silently missed.
    /// </summary>
    private DateTime? ParseNextOccurrence(string cronExpression)
    {
        try
        {
            var schedule = CrontabSchedule.Parse(cronExpression);
            // Look back by PollInterval: returns the most recent occurrence in that window,
            // or the next future one if the cron has not fired recently.
            return schedule.GetNextOccurrence(DateTime.UtcNow - PollInterval);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ScheduleService: failed to parse cron expression '{CronExpression}'",
                cronExpression);
            return null;
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
                        "ScheduleService: retry attempt {Attempt}/3, delay {DelaySeconds}s. Error: {Message}",
                        args.AttemptNumber + 1,
                        RetryDelays[Math.Min(args.AttemptNumber, RetryDelays.Length - 1)].TotalSeconds,
                        args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
}
