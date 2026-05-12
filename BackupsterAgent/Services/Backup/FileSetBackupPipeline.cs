using BackupsterAgent.Configuration;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Providers.Upload;
using BackupsterAgent.Services.Backup.Coordinator;
using BackupsterAgent.Services.Common.Resolvers;

namespace BackupsterAgent.Services.Backup;

public sealed class FileSetBackupPipeline
{
    private readonly StorageResolver _storages;
    private readonly IUploadProviderFactory _uploadFactory;
    private readonly FileBackupService _fileBackup;
    private readonly ManifestStore _manifestStore;
    private readonly ILogger<FileSetBackupPipeline> _logger;

    public FileSetBackupPipeline(
        StorageResolver storages,
        IUploadProviderFactory uploadFactory,
        FileBackupService fileBackup,
        ManifestStore manifestStore,
        ILogger<FileSetBackupPipeline> logger)
    {
        _storages = storages;
        _uploadFactory = uploadFactory;
        _fileBackup = fileBackup;
        _manifestStore = manifestStore;
        _logger = logger;
    }

    public async Task<PipelineOutcome> ExecuteAsync(
        BackupRunExecution exec,
        FileSetConfig config,
        CancellationToken ct)
    {
        var startedAt = exec.StartedAt;
        var uploader = _uploadFactory.GetProvider(config.StorageName);
        var backupFolder = $"{config.Name}/{startedAt:yyyy-MM-dd_HH-mm-ss}";

        _logger.LogInformation("FileSetBackupPipeline resolved. Folder: '{Folder}'", backupFolder);

        exec.Reporter.Report(BackupStage.CapturingFiles);

        await using var writer = _manifestStore.OpenWriter(config.Name, dumpObjectKey: string.Empty);
        var capture = await _fileBackup.CaptureAsync(config.Paths, uploader, writer, exec.Reporter, ct);
        var manifestKey = await writer.CompleteAsync(uploader, backupFolder, ct);

        var metrics = new FileBackupMetrics
        {
            ManifestKey = manifestKey,
            FilesCount = checked((int)writer.FilesCount),
            FilesTotalBytes = writer.FilesTotalBytes,
            NewChunksCount = capture.NewChunksCount,
        };
        var durationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;

        _logger.LogInformation(
            "FileSetBackupPipeline captured. FileSet: '{Name}', Files: {FilesCount}, TotalBytes: {TotalBytes}, NewChunks: {NewChunks}",
            config.Name, metrics.FilesCount, metrics.FilesTotalBytes, metrics.NewChunksCount);

        return new PipelineOutcome
        {
            Success = true,
            SizeBytes = metrics.FilesTotalBytes,
            DurationMs = durationMs,
            FileMetrics = metrics,
        };
    }
}
