using System.Net.Http.Json;
using BackupsterAgent.Configuration;
using BackupsterAgent.Contracts;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Common.State;
using Microsoft.Extensions.Options;
using NCrontab;
using Polly;

namespace BackupsterAgent.Services.Dashboard.Clients;

public sealed class ScheduleService : DashboardClientBase
{
    private readonly HttpClient _http;
    private readonly ScheduleStore _store;
    private readonly ILogger<ScheduleService> _logger;
    private readonly ResiliencePipeline _pipeline;

    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private ScheduleDto? _cachedSchedule;
    private DateTime _lastFetchAt = DateTime.MinValue;
    private readonly Dictionary<string, string> _lastCronByName = new(StringComparer.Ordinal);

    public ScheduleService(
        HttpClient http,
        ScheduleStore store,
        IOptions<AgentSettings> settings,
        IDashboardAuthGuard authGuard,
        ILogger<ScheduleService> logger)
        : base(settings.Value, authGuard)
    {
        _http = http;
        _store = store;
        _logger = logger;
        _pipeline = BuildRetryPipeline(nameof(ScheduleService), logger);

        _cachedSchedule = _store.TryLoad();
        if (_cachedSchedule is not null)
            _logger.LogInformation("ScheduleService: loaded schedule from disk cache.");
    }

    public async Task<IReadOnlyList<ScheduleEntry>> GetDueSchedulesAsync(
        string scheduleKey, CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastFetchAt >= PollInterval)
            await RefreshScheduleAsync(ct);

        if (_cachedSchedule?.Overrides is null)
            return Array.Empty<ScheduleEntry>();

        var result = new List<ScheduleEntry>(2);

        foreach (var o in _cachedSchedule.Overrides)
        {
            if (!string.Equals(o.DatabaseName, scheduleKey, StringComparison.Ordinal))
                continue;

            if (!o.IsActive)
                continue;

            var next = ParseNextOccurrence(o.CronExpression);
            if (next is null)
                continue;

            if (o.StorageNames is null || o.StorageNames.Count == 0)
            {
                result.Add(new ScheduleEntry(o.BackupMode, next.Value, null));
                continue;
            }

            foreach (var storage in o.StorageNames)
            {
                if (string.IsNullOrWhiteSpace(storage))
                    continue;

                result.Add(new ScheduleEntry(o.BackupMode, next.Value, storage));
            }
        }

        return result;
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
                fetched = await response.Content.ReadFromJsonAsync<ScheduleDto>(JsonOptions, innerCt);
            }, ct);

            if (fetched is null) return;

            LogCronChanges(fetched);

            _cachedSchedule = fetched;
            _lastFetchAt = DateTime.UtcNow;
            _store.Write(fetched);
        }
        catch (Exception ex)
        {
            _lastFetchAt = DateTime.UtcNow;
            _logger.LogWarning("ScheduleService: dashboard unavailable, using last known schedule.");
        }
    }

    private void LogCronChanges(ScheduleDto fetched)
    {
        var incoming = new Dictionary<string, string>(StringComparer.Ordinal);

        if (fetched.Overrides is not null)
        {
            foreach (var o in fetched.Overrides)
            {
                var value = o.IsActive ? o.CronExpression : "(inactive)";

                if (o.StorageNames is null || o.StorageNames.Count == 0)
                {
                    var key = $"{o.DatabaseName}|{o.BackupMode}|(default)";
                    incoming[key] = value;
                    continue;
                }

                foreach (var storage in o.StorageNames)
                {
                    if (string.IsNullOrWhiteSpace(storage))
                        continue;

                    var key = $"{o.DatabaseName}|{o.BackupMode}|{storage}";
                    incoming[key] = value;
                }
            }
        }

        foreach (var (key, newCron) in incoming)
        {
            var oldCron = _lastCronByName.GetValueOrDefault(key);
            if (oldCron == newCron) continue;

            _logger.LogInformation(
                "ScheduleService: cron for override '{Key}' changed. Old: '{OldCron}' → New: '{NewCron}'",
                key, oldCron ?? "(none)", newCron);
        }

        foreach (var key in _lastCronByName.Keys.ToList())
        {
            if (incoming.ContainsKey(key)) continue;

            _logger.LogInformation(
                "ScheduleService: cron for override '{Key}' removed (was '{OldCron}')",
                key, _lastCronByName[key]);
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
