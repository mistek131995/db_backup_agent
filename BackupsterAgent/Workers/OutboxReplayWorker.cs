using BackupsterAgent.Contracts;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Dashboard;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Workers;

public sealed class OutboxReplayWorker : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 100;

    private readonly IOutboxStore _store;
    private readonly IBackupRecordClient _client;
    private readonly OutboxSettings _settings;
    private readonly ILogger<OutboxReplayWorker> _logger;

    public OutboxReplayWorker(
        IOutboxStore store,
        IBackupRecordClient client,
        IOptions<OutboxSettings> settings,
        ILogger<OutboxReplayWorker> logger)
    {
        _store = store;
        _client = client;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(10, _settings.ReplayIntervalSeconds));

        _logger.LogInformation(
            "OutboxReplayWorker started. Interval: {IntervalSec}s, first tick in {StartupSec}s.",
            interval.TotalSeconds, StartupDelay.TotalSeconds);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OutboxReplayWorker: unexpected error in replay tick.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("OutboxReplayWorker stopped.");
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var entries = await _store.ListAsync(ct);
        if (entries.Count == 0) return;

        _logger.LogInformation("OutboxReplayWorker: {Count} entry(ies) queued for replay.", entries.Count);

        for (var i = 0; i < entries.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var entry = entries[i];

            if (entry.AttemptCount > MaxAttempts)
            {
                _logger.LogWarning(
                    "OutboxReplayWorker: entry {ClientTaskId} exceeded max attempts ({Max}), moving to dead-letter.",
                    entry.ClientTaskId, MaxAttempts);
                await _store.MoveToDeadAsync(entry.ClientTaskId, $"exceeded {MaxAttempts} replay attempts", ct);
                continue;
            }

            var disposition = await ProcessEntryAsync(entry, ct);
            if (disposition == TickDisposition.StopTick)
            {
                _logger.LogInformation(
                    "OutboxReplayWorker: dashboard offline, pausing until next tick (remaining: {Remaining}).",
                    entries.Count - i - 1);
                return;
            }
        }
    }

    private async Task<TickDisposition> ProcessEntryAsync(OutboxEntry entry, CancellationToken ct)
    {
        var current = entry;

        if (current.ServerRecordId is null)
        {
            var open = await _client.OpenAsync(BuildOpenDto(current), ct);

            switch (open.Status)
            {
                case DashboardAvailability.Ok:
                    if (open.Id is null || open.Id == Guid.Empty)
                    {
                        _logger.LogWarning(
                            "OutboxReplayWorker: open returned Ok with empty id for {ClientTaskId} — moving to dead-letter.",
                            current.ClientTaskId);
                        await _store.MoveToDeadAsync(current.ClientTaskId, "open returned empty id", ct);
                        return TickDisposition.Continue;
                    }
                    current = current with { ServerRecordId = open.Id, AttemptCount = 0 };
                    await _store.EnqueueAsync(current, ct);
                    _logger.LogInformation(
                        "OutboxReplayWorker: opened record {RecordId} for {ClientTaskId}.",
                        open.Id, current.ClientTaskId);
                    break;

                case DashboardAvailability.OfflineRetryable:
                    current = current with { AttemptCount = current.AttemptCount + 1 };
                    await _store.EnqueueAsync(current, ct);
                    return TickDisposition.StopTick;

                case DashboardAvailability.PermanentSkip:
                    _logger.LogWarning(
                        "OutboxReplayWorker: open rejected for {ClientTaskId} (permanent) — moving to dead-letter.",
                        current.ClientTaskId);
                    await _store.MoveToDeadAsync(current.ClientTaskId, "open rejected as permanent", ct);
                    return TickDisposition.Continue;
            }
        }

        var finalize = await _client.FinalizeAsync(current.ServerRecordId!.Value, BuildFinalizeDto(current), ct);

        switch (finalize.Status)
        {
            case DashboardAvailability.Ok:
                await _store.RemoveAsync(current.ClientTaskId, ct);
                _logger.LogInformation(
                    "OutboxReplayWorker: finalized record {RecordId} for {ClientTaskId} — entry removed.",
                    current.ServerRecordId, current.ClientTaskId);
                return TickDisposition.Continue;

            case DashboardAvailability.OfflineRetryable:
                current = current with { AttemptCount = current.AttemptCount + 1 };
                await _store.EnqueueAsync(current, ct);
                return TickDisposition.StopTick;

            case DashboardAvailability.PermanentSkip:
            default:
                _logger.LogWarning(
                    "OutboxReplayWorker: finalize rejected for {ClientTaskId} (permanent) — moving to dead-letter.",
                    current.ClientTaskId);
                await _store.MoveToDeadAsync(current.ClientTaskId, "finalize rejected as permanent", ct);
                return TickDisposition.Continue;
        }
    }

    private static OpenBackupRecordDto BuildOpenDto(OutboxEntry entry) => new()
    {
        DatabaseName = entry.DatabaseName,
        ConnectionName = entry.ConnectionName,
        StorageName = entry.StorageName,
        StartedAt = entry.StartedAt,
    };

    private static FinalizeBackupRecordDto BuildFinalizeDto(OutboxEntry entry) => new()
    {
        Status = string.Equals(entry.Status, "success", StringComparison.OrdinalIgnoreCase)
            ? BackupStatus.Success
            : BackupStatus.Failed,
        SizeBytes = entry.SizeBytes,
        DurationMs = entry.DurationMs,
        DumpObjectKey = entry.DumpObjectKey,
        ErrorMessage = entry.ErrorMessage,
        BackupAt = entry.BackupAt,
        ManifestKey = entry.ManifestKey,
        FilesCount = entry.FilesCount,
        FilesTotalBytes = entry.FilesTotalBytes,
        NewChunksCount = entry.NewChunksCount,
        FileBackupError = entry.FileBackupError,
    };

    private enum TickDisposition
    {
        Continue,
        StopTick,
    }
}
