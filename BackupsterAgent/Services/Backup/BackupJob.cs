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
    private readonly IAgentActivityLock _activityLock;
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
        IAgentActivityLock activityLock,
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
        _activityLock = activityLock;
        _agentSettings = agentSettings.Value;
        _activitySource = activitySource;
        _logger = logger;
    }

    public async Task<BackupResult> RunAsync(DatabaseConfig config, CancellationToken ct)
    {
        using var _ = await _activityLock.AcquireAsync($"backup:{config.Database}", ct);

        using var activity = _activitySource.StartActivity("backup.run");
        activity?.SetTag("database", config.Database);
        activity?.SetTag("connection", config.ConnectionName);
        activity?.SetTag("storage", config.StorageName);

        _logger.LogInformation(
            "BackupJob starting. Database: '{Database}', Connection: '{Connection}', Storage: '{Storage}', TraceId: {TraceId}",
            config.Database, config.ConnectionName, config.StorageName, activity?.TraceId.ToString() ?? "-");

        var recordId = await _recordClient.OpenAsync(
            new OpenBackupRecordDto
            {
                DatabaseName = config.Database,
                ConnectionName = config.ConnectionName,
            }, ct);

        if (recordId is null)
        {
            _logger.LogWarning(
                "BackupJob: could not open backup record on dashboard for '{Database}'. Skipping this run.",
                config.Database);
            return new BackupResult
            {
                Success = false,
                ErrorMessage = "Could not open backup record on dashboard — run skipped.",
            };
        }

        await using var reporter = _reporterFactory.CreateForBackup(recordId.Value);

        string? dumpFile = null;
        string? encryptedFile = null;
        string? dumpObjectKey = null;
        string? backupFolder = null;
        StorageConfig? storage = null;
        IUploadService? uploader = null;
        BackupResult result = new() { Success = false, ErrorMessage = "Unknown error" };

        try
        {
            var connection = _connections.Resolve(config.ConnectionName);
            storage = _storages.Resolve(config.StorageName);
            var provider = _factory.GetProvider(connection.DatabaseType);
            uploader = _uploadFactory.GetService(config.StorageName);
            backupFolder = $"{config.Database}/{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}";

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
            };

            _logger.LogInformation(
                "Dump uploaded. File: '{FilePath}', Size: {SizeBytes} bytes, " +
                "Duration: {DurationMs} ms, DumpObjectKey: '{DumpObjectKey}'",
                result.FilePath, result.SizeBytes, result.DurationMs, result.DumpObjectKey);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("BackupJob was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BackupJob failed");
            result = new BackupResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            TryDelete(dumpFile);
            TryDelete(encryptedFile);
        }

        FileBackupMetrics? fileMetrics = null;
        string? fileError = null;

        if (storage is not null && uploader is not null && backupFolder is not null)
        {
            (fileMetrics, fileError) = await CaptureFilesSafelyAsync(
                config, storage, uploader, backupFolder, dumpObjectKey, reporter, ct);
        }

        await _recordClient.FinalizeAsync(
            recordId.Value,
            BuildFinalizeDto(result, fileMetrics, fileError),
            ct);

        return result;
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
            var capture = await _fileBackup.CaptureAsync(config.FilePaths, uploader, reporter, ct);
            var manifest = capture.Manifest with
            {
                Database = config.Database,
                DumpObjectKey = dumpObjectKey ?? string.Empty,
            };
            var manifestKey = await _manifestStore.SaveAsync(manifest, backupFolder, uploader, ct);

            var metrics = new FileBackupMetrics
            {
                ManifestKey = manifestKey,
                FilesCount = manifest.Files.Count,
                FilesTotalBytes = manifest.Files.Sum(f => f.Size),
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
