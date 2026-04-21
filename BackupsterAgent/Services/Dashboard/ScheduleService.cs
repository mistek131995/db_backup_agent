using System.Net.Http.Json;
using BackupsterAgent.Contracts;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;
using NCrontab;
using Polly;

namespace BackupsterAgent.Services.Dashboard;

public sealed class ScheduleService : DashboardClientBase
{
    private readonly HttpClient _http;
    private readonly ILogger<ScheduleService> _logger;
    private readonly ResiliencePipeline _pipeline;

    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private const string DefaultKey = "__default__";

    private ScheduleDto? _cachedSchedule;
    private DateTime _lastFetchAt = DateTime.MinValue;
    private readonly Dictionary<string, string> _lastCronByName = new(StringComparer.Ordinal);

    public ScheduleService(
        HttpClient http,
        IOptions<AgentSettings> settings,
        IDashboardAuthGuard authGuard,
        ILogger<ScheduleService> logger)
        : base(settings.Value, authGuard)
    {
        _http = http;
        _logger = logger;
        _pipeline = BuildRetryPipeline(nameof(ScheduleService), logger);
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
        if (!IsConfigured(_logger, nameof(ScheduleService)))
        {
            _lastFetchAt = DateTime.UtcNow;
            return;
        }

        try
        {
            ScheduleDto? fetched = null;

            await _pipeline.ExecuteAsync(async innerCt =>
            {
                var url = $"{Settings.DashboardUrl.TrimEnd('/')}/api/v1/agent/schedule";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-Agent-Token", Settings.Token);

                var response = await _http.SendAsync(request, innerCt);
                ThrowIfUnauthorized(response, $"{nameof(ScheduleService)}.{nameof(RefreshScheduleAsync)}", _logger);
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
}
