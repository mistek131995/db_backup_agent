using BackupsterAgent.Configuration;
using BackupsterAgent.Contracts;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Backup.Coordinator;

namespace BackupsterAgent.Services.Backup;

internal sealed class FileSetBackupDescriptor : IBackupRunDescriptor
{
    private readonly FileSetConfig _config;
    private readonly FileSetBackupPipeline _pipeline;

    public FileSetBackupDescriptor(FileSetConfig config, FileSetBackupPipeline pipeline)
    {
        _config = config;
        _pipeline = pipeline;
    }

    public string DisplayName => $"FileSetBackupJob[{_config.Name}]";

    public string ActivityName => "fileset.backup.run";

    public IReadOnlyList<KeyValuePair<string, string?>> ActivityTags =>
    [
        new("fileSet", _config.Name),
        new("storage", _config.StorageName),
    ];

    public OpenBackupRecordDto BuildOpenDto(DateTime startedAt) => new()
    {
        DatabaseName = _config.Name,
        ConnectionName = string.Empty,
        StorageName = _config.StorageName,
        StartedAt = startedAt,
        DatabaseType = DatabaseType.FileSet,
        FileSetName = _config.Name,
    };

    public Task<PipelineOutcome> ExecuteAsync(BackupRunExecution exec, CancellationToken ct) =>
        _pipeline.ExecuteAsync(exec, _config, ct);

    public OutboxEntry BuildOutboxEntry(
        string clientTaskId,
        DateTime startedAt,
        FinalizeBackupRecordDto finalize,
        Guid? serverRecordId) => new()
    {
        ClientTaskId = clientTaskId,
        DatabaseName = _config.Name,
        ConnectionName = string.Empty,
        StorageName = _config.StorageName,
        StartedAt = startedAt,
        BackupAt = finalize.BackupAt,
        Status = finalize.Status == BackupStatus.Success ? "success" : "failed",
        SizeBytes = finalize.SizeBytes,
        DurationMs = finalize.DurationMs,
        DumpObjectKey = finalize.DumpObjectKey,
        ErrorMessage = finalize.ErrorMessage,
        ManifestKey = finalize.ManifestKey,
        FilesCount = finalize.FilesCount,
        FilesTotalBytes = finalize.FilesTotalBytes,
        NewChunksCount = finalize.NewChunksCount,
        FileBackupError = finalize.FileBackupError,
        QueuedAt = DateTime.UtcNow,
        AttemptCount = 0,
        ServerRecordId = serverRecordId,
        DatabaseType = DatabaseType.FileSet,
        FileSetName = _config.Name,
    };
}
