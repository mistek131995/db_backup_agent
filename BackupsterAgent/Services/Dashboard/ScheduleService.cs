using System.Net;
using System.Net.Http.Json;
using BackupsterAgent.Contracts;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;
using NCrontab;
using Polly;
using Polly.Retry;

namespace BackupsterAgent.Services.Dashboard;

public sealed class ScheduleService
{
    private readonly HttpClient _http;
    private readonly AgentSettings _settings;
    private readonly IDashboardAuthGuard _authGuard;
    private readonly ILogger<ScheduleService> _logger;
    private readonly ResiliencePipeline _pipeline;

    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private const string DefaultKey = "__default__";

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    private ScheduleDto? _cachedSchedule;
    private DateTime _lastFetchAt = DateTime.MinValue;
    private readonly Dictionary<string, string> _lastCronByName = new(StringComparer.Ordinal);

    public ScheduleService(
        HttpClient http,
        IOptions<AgentSettings> settings,
        IDashboardAuthGuard authGuard,
        ILogger<ScheduleService> logger)
    {
        _http = http;
        _settings = settings.Value;
        _authGuard = authGuard;
        _logger = logger;
        _pipeline = BuildPipeline();
    }

    public async Task<DateTime?> GetNextRunAsync(string databaseName, CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastFetchAt >= PollInterval)
            await RefreshScheduleAsync(ct);

        if (_cachedSchedule is null)
            return null;

        var match = _cachedSchedule.Overrides?
            .FirstOrDefault(o => string.Equals(o.DatabaseName, databaseName, StringComparison.Ordinal));

        if (match is not null && match.IsActive)
            return ParseNextOccurrence(match.CronExpression);

        if (!_cachedSchedule.IsActive)
            return null;

        return ParseNextOccurrence(_cachedSchedule.CronExpression);
    }

    private async Task RefreshScheduleAsync(CancellationToken ct)
    {
        if (!IsConfigured())
        {
            _lastFetchAt = DateTime.UtcNow;
            return;
        }

        try
        {
            ScheduleDto? fetched = null;

            await _pipeline.ExecuteAsync(async innerCt =>
            {
                var url = $"{_settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/schedule";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-Agent-Token", _settings.Token);

                var response = await _http.SendAsync(request, innerCt);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _authGuard.OnUnauthorized($"{nameof(ScheduleService)}.{nameof(RefreshScheduleAsync)}", _logger);
                    throw new DashboardUnauthorizedException($"{nameof(ScheduleService)}.{nameof(RefreshScheduleAsync)}");
                }
                response.EnsureSuccessStatusCode();
                fetched = await response.Content.ReadFromJsonAsync<ScheduleDto>(innerCt);
            }, ct);

            if (fetched is null) return;

            LogCronChanges(fetched);

            _cachedSchedule = fetched;
            _lastFetchAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _lastFetchAt = DateTime.UtcNow;
            _logger.LogWarning(ex,
                "ScheduleService: failed to fetch schedule from server. Using last known schedule.");
        }
    }

    private bool IsConfigured()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Token) && !string.IsNullOrWhiteSpace(_settings.DashboardUrl))
            return true;

        _logger.LogWarning(
            "ScheduleService: AgentSettings.Token or DashboardUrl is not configured. Schedule fetching is disabled.");
        return false;
    }

    private void LogCronChanges(ScheduleDto fetched)
    {
        var incoming = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DefaultKey] = fetched.IsActive ? fetched.CronExpression : "(inactive)",
        };

        if (fetched.Overrides is not null)
        {
            foreach (var o in fetched.Overrides)
                incoming[o.DatabaseName] = o.IsActive ? o.CronExpression : "(inactive)";
        }

        foreach (var (name, newCron) in incoming)
        {
            var oldCron = _lastCronByName.GetValueOrDefault(name);
            if (oldCron == newCron) continue;

            var label = name == DefaultKey ? "default" : $"override '{name}'";
            _logger.LogInformation(
                "ScheduleService: cron for {Label} changed. Old: '{OldCron}' → New: '{NewCron}'",
                label, oldCron ?? "(none)", newCron);
        }

        foreach (var name in _lastCronByName.Keys.ToList())
        {
            if (incoming.ContainsKey(name)) continue;

            var label = name == DefaultKey ? "default" : $"override '{name}'";
            _logger.LogInformation(
                "ScheduleService: cron for {Label} removed (was '{OldCron}')",
                label, _lastCronByName[name]);
        }

        _lastCronByName.Clear();
        foreach (var (k, v) in incoming)
            _lastCronByName[k] = v;
    }

    private DateTime? ParseNextOccurrence(string cronExpression)
    {
        try
        {
            var schedule = CrontabSchedule.Parse(cronExpression);
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
                        "ScheduleService: retry attempt {Attempt}/3, delay {DelaySeconds}s. Error: {Message}",
                        args.AttemptNumber + 1,
                        RetryDelays[Math.Min(args.AttemptNumber, RetryDelays.Length - 1)].TotalSeconds,
                        args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
}
