using BackupsterAgent.Configuration;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Providers.Backup;
using BackupsterAgent.Providers.Upload;
using BackupsterAgent.Services.Backup.Coordinator;
using BackupsterAgent.Services.Common.Progress;
using BackupsterAgent.Services.Common.Resolvers;
using BackupsterAgent.Services.Common.Security;

namespace BackupsterAgent.Services.Backup;

public sealed class DatabaseBackupPipeline
{
    private readonly IBackupProviderFactory _factory;
    private readonly ConnectionResolver _connections;
    private readonly StorageResolver _storages;
    private readonly EncryptionService _encryption;
    private readonly IUploadProviderFactory _uploadFactory;
    private readonly FileBackupService _fileBackup;
    private readonly ManifestStore _manifestStore;
    private readonly ILogger<DatabaseBackupPipeline> _logger;

    public DatabaseBackupPipeline(
        IBackupProviderFactory factory,
        ConnectionResolver connections,
        StorageResolver storages,
        EncryptionService encryption,
        IUploadProviderFactory uploadFactory,
        FileBackupService fileBackup,
        ManifestStore manifestStore,
        ILogger<DatabaseBackupPipeline> logger)
    {
        _factory = factory;
        _connections = connections;
        _storages = storages;
        _encryption = encryption;
        _uploadFactory = uploadFactory;
        _fileBackup = fileBackup;
        _manifestStore = manifestStore;
        _logger = logger;
    }

    public async Task<PipelineOutcome> ExecuteAsync(
        BackupRunExecution exec,
        DatabaseConfig config,
        BackupMode mode,
        CancellationToken ct)
    {
        var startedAt = exec.StartedAt;
        string? dumpFile = null;
        string? encryptedFile = null;
        string? dumpObjectKey = null;
        string? backupFolder = null;
        StorageConfig storage;
        IUploadProvider uploader;
        long sizeBytes;
        long durationMs;

        try
        {
            var connection = _connections.Resolve(config.ConnectionName);
            storage = _storages.Resolve(config.StorageName);
            var provider = _factory.GetProvider(connection.DatabaseType, mode);
            uploader = _uploadFactory.GetProvider(config.StorageName);
            backupFolder = $"{config.Database}/{startedAt:yyyy-MM-dd_HH-mm-ss}";

            _logger.LogInformation(
                "DatabaseBackupPipeline resolved. Provider: {ProviderType}, Folder: '{Folder}'",
                provider.GetType().Name, backupFolder);

            await provider.ValidatePermissionsAsync(connection, config.Database, ct);

            _logger.LogInformation("Step 1/3: dump");
            exec.Reporter.Report(BackupStage.Dumping);
            var dumpResult = await provider.BackupAsync(config, connection, ct);
            dumpFile = dumpResult.FilePath;
            sizeBytes = dumpResult.SizeBytes;
            durationMs = dumpResult.DurationMs;

            _logger.LogInformation("Step 2/3: encrypt");
            exec.Reporter.Report(BackupStage.EncryptingDump);
            encryptedFile = await _encryption.EncryptAsync(dumpFile, ct);

            _logger.LogInformation("Step 3/3: upload");
            exec.Reporter.Report(BackupStage.UploadingDump, processed: 0, unit: "bytes");
            var uploadProgress = new Progress<long>(bytes =>
                exec.Reporter.Report(BackupStage.UploadingDump, processed: bytes, unit: "bytes"));
            await uploader.UploadAsync(encryptedFile, backupFolder, uploadProgress, ct);
            dumpObjectKey = $"{backupFolder}/{Path.GetFileName(encryptedFile)}";

            _logger.LogInformation(
                "Dump uploaded. File: '{FilePath}', Size: {SizeBytes} bytes, " +
                "Duration: {DurationMs} ms, DumpObjectKey: '{DumpObjectKey}'",
                dumpFile, sizeBytes, durationMs, dumpObjectKey);
        }
        finally
        {
            TryDelete(dumpFile);
            TryDelete(encryptedFile);
        }

        var (fileMetrics, fileError) = await CaptureFilesSafelyAsync(
            config, storage, uploader, backupFolder, dumpObjectKey, exec.Reporter, ct);

        return new PipelineOutcome
        {
            Success = true,
            FilePath = dumpFile,
            SizeBytes = sizeBytes,
            DurationMs = durationMs,
            DumpObjectKey = dumpObjectKey,
            FileMetrics = fileMetrics,
            FileBackupError = fileError,
        };
    }

    internal async Task<(FileBackupMetrics? Metrics, string? Error)> CaptureFilesSafelyAsync(
        DatabaseConfig config,
        StorageConfig storage,
        IUploadProvider uploader,
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
}
