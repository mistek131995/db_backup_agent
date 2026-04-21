using System.Diagnostics;
using BackupsterAgent.Configuration;
using BackupsterAgent.Contracts;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Providers;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Dashboard;
using BackupsterAgent.Services.Upload;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Services.Backup;

public sealed class BackupJob
{
    private readonly IBackupProviderFactory _factory;
    private readonly ConnectionResolver _connections;
    private readonly StorageResolver _storages;
    private readonly EncryptionService _encryption;
    private readonly IUploadServiceFactory _uploadFactory;
    private readonly FileBackupService _fileBackup;
    private readonly ManifestStore _manifestStore;
    private readonly IBackupRecordClient _recordClient;
    private readonly IProgressReporterFactory _reporterFactory;
    private readonly IOutboxStore _outboxStore;
    private readonly AgentSettings _agentSettings;
    private readonly ActivitySource _activitySource;
    private readonly ILogger<BackupJob> _logger;

    public BackupJob(
        IBackupProviderFactory factory,
        ConnectionResolver connections,
        StorageResolver storages,
        EncryptionService encryption,
        IUploadServiceFactory uploadFactory,
        FileBackupService fileBackup,
        ManifestStore manifestStore,
        IBackupRecordClient recordClient,
        IProgressReporterFactory reporterFactory,
        IOutboxStore outboxStore,
        IOptions<AgentSettings> agentSettings,
        ActivitySource activitySource,
        ILogger<BackupJob> logger)
    {
        _factory = factory;
        _connections = connections;
        _storages = storages;
        _encryption = encryption;
        _uploadFactory = uploadFactory;
        _fileBackup = fileBackup;
        _manifestStore = manifestStore;
        _recordClient = recordClient;
        _reporterFactory = reporterFactory;
        _outboxStore = outboxStore;
        _agentSettings = agentSettings.Value;
        _activitySource = activitySource;
        _logger = logger;
    }

    public async Task<BackupResult> RunAsync(DatabaseConfig config, CancellationToken ct)
    {
        using var activity = _activitySource.StartActivity("backup.run");
        activity?.SetTag("database", config.Database);
        activity?.SetTag("connection", config.ConnectionName);
        activity?.SetTag("storage", config.StorageName);

        _logger.LogInformation(
            "BackupJob starting. Database: '{Database}', Connection: '{Connection}', Storage: '{Storage}', TraceId: {TraceId}",
            config.Database, config.ConnectionName, config.StorageName, activity?.TraceId.ToString() ?? "-");

        var startedAt = DateTime.UtcNow;

        var openResult = await OpenRecordAsync(config, startedAt, ct);

        if (openResult.Status == DashboardAvailability.PermanentSkip)
        {
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
                "BackupJob: dashboard offline at start for '{Database}' — switching to offline mode (clientTaskId={ClientTaskId})",
                config.Database, clientTaskId);
        }

        await using var reporter = _reporterFactory.CreateForBackup(recordId ?? Guid.Empty, offline: offline);

        string? dumpFile = null;
        string? encryptedFile = null;
        string? dumpObjectKey = null;
        string? backupFolder = null;
        StorageConfig? storage = null;
        IUploadService? uploader = null;
        BackupResult result = new() { Success = false, ErrorMessage = "Unknown error" };
        bool cancelled = false;

        try
        {
            var connection = _connections.Resolve(config.ConnectionName);
            storage = _storages.Resolve(config.StorageName);
            var provider = _factory.GetProvider(connection.DatabaseType);
            uploader = _uploadFactory.GetService(config.StorageName);
            backupFolder = $"{config.Database}/{startedAt:yyyy-MM-dd_HH-mm-ss}";

            _logger.LogInformation(
                "BackupJob resolved. Provider: {ProviderType}, Folder: '{Folder}'",
                provider.GetType().Name, backupFolder);

            _logger.LogInformation("Step 1/3: dump");
            reporter.Report(BackupStage.Dumping);
            var dumpResult = await provider.BackupAsync(config, connection, ct);
            dumpFile = dumpResult.FilePath;

            _logger.LogInformation("Step 2/3: encrypt");
            reporter.Report(BackupStage.EncryptingDump);
            encryptedFile = await _encryption.EncryptAsync(dumpFile, ct);

            _logger.LogInformation("Step 3/3: upload");
            reporter.Report(BackupStage.UploadingDump, processed: 0, unit: "bytes");
            var uploadProgress = new Progress<long>(bytes =>
                reporter.Report(BackupStage.UploadingDump, processed: bytes, unit: "bytes"));
            await uploader.UploadAsync(encryptedFile, backupFolder, uploadProgress, ct);
            dumpObjectKey = $"{backupFolder}/{Path.GetFileName(encryptedFile)}";

            result = new BackupResult
            {
                FilePath = dumpFile,
                SizeBytes = dumpResult.SizeBytes,
                DurationMs = dumpResult.DurationMs,
                Success = true,
                DumpObjectKey = dumpObjectKey,
                BackupRecordId = recordId,
            };

            _logger.LogInformation(
                "Dump uploaded. File: '{FilePath}', Size: {SizeBytes} bytes, " +
                "Duration: {DurationMs} ms, DumpObjectKey: '{DumpObjectKey}'",
                result.FilePath, result.SizeBytes, result.DurationMs, result.DumpObjectKey);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("BackupJob cancelled mid-pipeline for '{Database}'", config.Database);
            result = new BackupResult
            {
                Success = false,
                ErrorMessage = "Бэкап прерван: агент остановлен.",
                BackupRecordId = recordId,
            };
            cancelled = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BackupJob failed");
            result = new BackupResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                BackupRecordId = recordId,
            };
        }
        finally
        {
            TryDelete(dumpFile);
            TryDelete(encryptedFile);
        }

        FileBackupMetrics? fileMetrics = null;
        string? fileError = null;

        if (!result.Success)
        {
            if (config.FilePaths.Count > 0)
            {
                _logger.LogWarning(
                    "File backup skipped for database '{Database}' because dump stage failed. " +
                    "Invariant: БД и файлы восстанавливаются вместе — без дампа файлы не сохраняются.",
                    config.Database);
            }
        }
        else if (storage is not null && uploader is not null && backupFolder is not null)
        {
            (fileMetrics, fileError) = await CaptureFilesSafelyAsync(
                config, storage, uploader, backupFolder, dumpObjectKey, reporter, ct);
        }

        var finalizeDto = BuildFinalizeDto(result, fileMetrics, fileError);

        if (offline)
        {
            await EnqueueOutboxAsync(
                clientTaskId!, config, startedAt, finalizeDto, serverRecordId: null, ct);
        }
        else
        {
            var finalizeResult = await FinalizeRecordAsync(
                recordId!.Value, finalizeDto, ct, cancelled, config.Database);

            if (finalizeResult.Status == DashboardAvailability.OfflineRetryable && !cancelled)
            {
                clientTaskId = Guid.NewGuid().ToString();
                _logger.LogWarning(
                    "BackupJob: dashboard offline at finalize for '{Database}' — entry queued (clientTaskId={ClientTaskId}, serverRecordId={RecordId})",
                    config.Database, clientTaskId, recordId);
                await EnqueueOutboxAsync(
                    clientTaskId, config, startedAt, finalizeDto, serverRecordId: recordId, ct);
            }
        }

        if (cancelled) throw new OperationCanceledException(ct);

        return result;
    }

    private async Task<OpenRecordResult> OpenRecordAsync(
        DatabaseConfig config, DateTime startedAt, CancellationToken ct)
    {
        var result = await _recordClient.OpenAsync(
            new OpenBackupRecordDto
            {
                DatabaseName = config.Database,
                ConnectionName = config.ConnectionName,
                StorageName = config.StorageName,
                StartedAt = startedAt,
            }, ct);

        if (result.Status == DashboardAvailability.PermanentSkip)
        {
            _logger.LogWarning(
                "BackupJob: dashboard rejected open for '{Database}' (permanent skip). Run skipped.",
                config.Database);
        }

        return result;
    }

    private async Task<FinalizeRecordResult> FinalizeRecordAsync(
        Guid recordId,
        FinalizeBackupRecordDto dto,
        CancellationToken runCt,
        bool cancelled,
        string database)
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
                "BackupJob: could not finalize cancelled record for '{Database}'. Sweeper will close it.",
                database);
            return new FinalizeRecordResult(DashboardAvailability.PermanentSkip);
        }
    }

    private async Task EnqueueOutboxAsync(
        string clientTaskId,
        DatabaseConfig config,
        DateTime startedAt,
        FinalizeBackupRecordDto finalizeDto,
        Guid? serverRecordId,
        CancellationToken ct)
    {
        var entry = new OutboxEntry
        {
            ClientTaskId = clientTaskId,
            DatabaseName = config.Database,
            ConnectionName = config.ConnectionName,
            StorageName = config.StorageName,
            StartedAt = startedAt,
            BackupAt = finalizeDto.BackupAt,
            Status = finalizeDto.Status == BackupStatus.Success ? "success" : "failed",
            SizeBytes = finalizeDto.SizeBytes,
            DurationMs = finalizeDto.DurationMs,
            DumpObjectKey = finalizeDto.DumpObjectKey,
            ErrorMessage = finalizeDto.ErrorMessage,
            ManifestKey = finalizeDto.ManifestKey,
            FilesCount = finalizeDto.FilesCount,
            FilesTotalBytes = finalizeDto.FilesTotalBytes,
            NewChunksCount = finalizeDto.NewChunksCount,
            FileBackupError = finalizeDto.FileBackupError,
            QueuedAt = DateTime.UtcNow,
            AttemptCount = 0,
            ServerRecordId = serverRecordId,
        };

        try
        {
            await _outboxStore.EnqueueAsync(entry, ct);
            _logger.LogInformation(
                "BackupJob: outbox entry saved (clientTaskId={ClientTaskId}, database={Database}, status={Status}, serverRecordId={ServerId})",
                clientTaskId, config.Database, entry.Status, serverRecordId?.ToString() ?? "-");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "BackupJob: failed to save outbox entry for '{Database}'. Backup completed but won't be replayed.",
                config.Database);
        }
    }

    internal async Task<(FileBackupMetrics? Metrics, string? Error)> CaptureFilesSafelyAsync(
        DatabaseConfig config,
        StorageConfig storage,
        IUploadService uploader,
        string backupFolder,
        string? dumpObjectKey,
        IProgressReporter<BackupStage> reporter,
        CancellationToken ct)
    {
        if (config.FilePaths.Count == 0)
            return (null, null);

        if (storage.Provider == UploadProvider.Sftp)
        {
            _logger.LogWarning(
                "File backup is not supported on SFTP storage '{Storage}'. " +
                "Skipping {Count} file path(s) for database '{Database}'",
                storage.Name, config.FilePaths.Count, config.Database);
            return (null, $"Бэкап файлов не поддерживается на SFTP-хранилище '{storage.Name}'. Файлы не загружены.");
        }

        try
        {
            _logger.LogInformation(
                "Capturing file backup for database '{Database}' ({Count} path(s))",
                config.Database, config.FilePaths.Count);

            reporter.Report(BackupStage.CapturingFiles);

            await using var writer = _manifestStore.OpenWriter(
                config.Database,
                dumpObjectKey ?? string.Empty);

            var capture = await _fileBackup.CaptureAsync(config.FilePaths, uploader, writer, reporter, ct);
            var manifestKey = await writer.CompleteAsync(uploader, backupFolder, ct);

            var metrics = new FileBackupMetrics
            {
                ManifestKey = manifestKey,
                FilesCount = checked((int)writer.FilesCount),
                FilesTotalBytes = writer.FilesTotalBytes,
                NewChunksCount = capture.NewChunksCount,
            };
            return (metrics, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File backup failed for database '{Database}'", config.Database);
            return (null, "Не удалось загрузить файлы в хранилище. Подробности — в логах агента.");
        }
    }

    private static FinalizeBackupRecordDto BuildFinalizeDto(
        BackupResult result, FileBackupMetrics? fileMetrics, string? fileBackupError) =>
        new()
        {
            Status = result.Success ? BackupStatus.Success : BackupStatus.Failed,
            SizeBytes = result.Success ? result.SizeBytes : null,
            DurationMs = result.Success ? result.DurationMs : null,
            DumpObjectKey = result.DumpObjectKey,
            ErrorMessage = result.ErrorMessage,
            BackupAt = DateTime.UtcNow,
            ManifestKey = fileMetrics?.ManifestKey,
            FilesCount = fileMetrics?.FilesCount,
            FilesTotalBytes = fileMetrics?.FilesTotalBytes,
            NewChunksCount = fileMetrics?.NewChunksCount,
            FileBackupError = fileBackupError,
        };

    private void TryDelete(string? path)
    {
        if (path is null) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogDebug("Deleted local file '{Path}'", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete local file '{Path}'", path);
        }
    }

    internal sealed class FileBackupMetrics
    {
        public required string ManifestKey { get; init; }
        public required int FilesCount { get; init; }
        public required long FilesTotalBytes { get; init; }
        public required int NewChunksCount { get; init; }
    }
}
