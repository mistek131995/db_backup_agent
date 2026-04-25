using System.Diagnostics;
using BackupsterAgent.Contracts;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Common.Outbox;
using BackupsterAgent.Services.Common.Progress;
using BackupsterAgent.Services.Dashboard;
using BackupsterAgent.Services.Dashboard.Clients;

namespace BackupsterAgent.Services.Backup.Coordinator;

public sealed class BackupRunCoordinator
{
    private readonly IBackupRecordClient _recordClient;
    private readonly IProgressReporterFactory _reporterFactory;
    private readonly IOutboxStore _outboxStore;
    private readonly ActivitySource _activitySource;
    private readonly ILogger<BackupRunCoordinator> _logger;

    public BackupRunCoordinator(
        IBackupRecordClient recordClient,
        IProgressReporterFactory reporterFactory,
        IOutboxStore outboxStore,
        ActivitySource activitySource,
        ILogger<BackupRunCoordinator> logger)
    {
        _recordClient = recordClient;
        _reporterFactory = reporterFactory;
        _outboxStore = outboxStore;
        _activitySource = activitySource;
        _logger = logger;
    }

    public async Task<BackupResult> RunAsync(IBackupRunDescriptor descriptor, CancellationToken ct)
    {
        using var activity = _activitySource.StartActivity(descriptor.ActivityName);
        foreach (var tag in descriptor.ActivityTags)
            activity?.SetTag(tag.Key, tag.Value);

        _logger.LogInformation(
            "{Name} starting. TraceId: {TraceId}",
            descriptor.DisplayName, activity?.TraceId.ToString() ?? "-");

        var startedAt = DateTime.UtcNow;
        var openResult = await _recordClient.OpenAsync(descriptor.BuildOpenDto(startedAt), ct);

        if (openResult.Status == DashboardAvailability.PermanentSkip)
        {
            _logger.LogWarning(
                "{Name}: dashboard rejected open (permanent skip). Run skipped.",
                descriptor.DisplayName);
            return new BackupResult
            {
                Success = false,
                ErrorMessage = "Could not open backup record on dashboard — run skipped.",
            };
        }

        var offline = openResult.Status == DashboardAvailability.OfflineRetryable;
        var recordId = openResult.Id;
        string? clientTaskId = offline ? Guid.NewGuid().ToString() : null;

        if (offline)
        {
            _logger.LogWarning(
                "{Name}: dashboard offline at start — switching to offline mode (clientTaskId={ClientTaskId})",
                descriptor.DisplayName, clientTaskId);
        }

        await using var reporter = _reporterFactory.CreateForBackup(recordId ?? Guid.Empty, offline);

        PipelineOutcome outcome;
        bool cancelled = false;

        try
        {
            outcome = await descriptor.ExecuteAsync(
                new BackupRunExecution(recordId, offline, startedAt, reporter), ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{Name} cancelled mid-pipeline", descriptor.DisplayName);
            outcome = PipelineOutcome.Failed("Бэкап прерван: агент остановлен.");
            cancelled = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Name} failed", descriptor.DisplayName);
            outcome = PipelineOutcome.Failed(ex.Message);
        }

        var finalizeDto = BuildFinalizeDto(outcome);

        if (offline)
        {
            await EnqueueOutboxAsync(
                descriptor, clientTaskId!, startedAt, finalizeDto, serverRecordId: null, ct, cancelled);
        }
        else
        {
            var finalizeResult = await FinalizeRecordAsync(
                descriptor, recordId!.Value, finalizeDto, ct, cancelled);

            if (finalizeResult.Status == DashboardAvailability.OfflineRetryable && !cancelled)
            {
                clientTaskId = Guid.NewGuid().ToString();
                _logger.LogWarning(
                    "{Name}: dashboard offline at finalize — entry queued (clientTaskId={ClientTaskId}, serverRecordId={RecordId})",
                    descriptor.DisplayName, clientTaskId, recordId);
                await EnqueueOutboxAsync(
                    descriptor, clientTaskId, startedAt, finalizeDto, serverRecordId: recordId, ct, cancelled: false);
            }
        }

        if (cancelled) throw new OperationCanceledException(ct);

        return new BackupResult
        {
            FilePath = outcome.FilePath ?? string.Empty,
            SizeBytes = outcome.SizeBytes ?? 0,
            DurationMs = outcome.DurationMs ?? 0,
            Success = outcome.Success,
            ErrorMessage = outcome.ErrorMessage,
            DumpObjectKey = outcome.DumpObjectKey,
            BackupRecordId = recordId,
        };
    }

    private static FinalizeBackupRecordDto BuildFinalizeDto(PipelineOutcome outcome) => new()
    {
        Status = outcome.Success ? BackupStatus.Success : BackupStatus.Failed,
        SizeBytes = outcome.Success ? outcome.SizeBytes : null,
        DurationMs = outcome.Success ? outcome.DurationMs : null,
        DumpObjectKey = outcome.DumpObjectKey,
        ErrorMessage = outcome.ErrorMessage,
        BackupAt = DateTime.UtcNow,
        ManifestKey = outcome.FileMetrics?.ManifestKey,
        FilesCount = outcome.FileMetrics?.FilesCount,
        FilesTotalBytes = outcome.FileMetrics?.FilesTotalBytes,
        NewChunksCount = outcome.FileMetrics?.NewChunksCount,
        FileBackupError = outcome.FileBackupError,
    };

    private async Task<FinalizeRecordResult> FinalizeRecordAsync(
        IBackupRunDescriptor descriptor,
        Guid recordId,
        FinalizeBackupRecordDto dto,
        CancellationToken runCt,
        bool cancelled)
    {
        using var cancelFinalizeCts = cancelled ? new CancellationTokenSource(TimeSpan.FromSeconds(10)) : null;
        var finalizeCt = cancelled ? cancelFinalizeCts!.Token : runCt;

        try
        {
            return await _recordClient.FinalizeAsync(recordId, dto, finalizeCt);
        }
        catch (Exception ex) when (cancelled)
        {
            _logger.LogError(ex,
                "{Name}: could not finalize cancelled record. Sweeper will close it.",
                descriptor.DisplayName);
            return new FinalizeRecordResult(DashboardAvailability.PermanentSkip);
        }
    }

    private async Task EnqueueOutboxAsync(
        IBackupRunDescriptor descriptor,
        string clientTaskId,
        DateTime startedAt,
        FinalizeBackupRecordDto finalizeDto,
        Guid? serverRecordId,
        CancellationToken runCt,
        bool cancelled)
    {
        var entry = descriptor.BuildOutboxEntry(clientTaskId, startedAt, finalizeDto, serverRecordId);

        using var fallbackCts = cancelled ? new CancellationTokenSource(TimeSpan.FromSeconds(10)) : null;
        var enqueueCt = cancelled ? fallbackCts!.Token : runCt;

        try
        {
            await _outboxStore.EnqueueAsync(entry, enqueueCt);
            _logger.LogInformation(
                "{Name}: outbox entry saved (clientTaskId={ClientTaskId}, status={Status}, serverRecordId={ServerId})",
                descriptor.DisplayName, clientTaskId, entry.Status, serverRecordId?.ToString() ?? "-");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "{Name}: failed to save outbox entry. Backup completed but won't be replayed.",
                descriptor.DisplayName);
        }
    }
}
