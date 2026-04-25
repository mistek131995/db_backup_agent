using BackupsterAgent.Configuration;
using BackupsterAgent.Contracts;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Backup.Coordinator;

namespace BackupsterAgent.Services.Backup;

internal sealed class DatabaseBackupDescriptor : IBackupRunDescriptor
{
    private readonly DatabaseConfig _config;
    private readonly BackupMode _mode;
    private readonly DatabaseBackupPipeline _pipeline;

    public DatabaseBackupDescriptor(DatabaseConfig config, BackupMode mode, DatabaseBackupPipeline pipeline)
    {
        _config = config;
        _mode = mode;
        _pipeline = pipeline;
    }

    public string DisplayName => $"BackupJob[{_config.Database}]";

    public string ActivityName => "backup.run";

    public IReadOnlyList<KeyValuePair<string, string?>> ActivityTags =>
    [
        new("database", _config.Database),
        new("connection", _config.ConnectionName),
        new("storage", _config.StorageName),
        new("backupMode", _mode.ToString()),
    ];

    public OpenBackupRecordDto BuildOpenDto(DateTime startedAt) => new()
    {
        DatabaseName = _config.Database,
        ConnectionName = _config.ConnectionName,
        StorageName = _config.StorageName,
        StartedAt = startedAt,
        BackupMode = _mode,
    };

    public Task<PipelineOutcome> ExecuteAsync(BackupRunExecution exec, CancellationToken ct) =>
        _pipeline.ExecuteAsync(exec, _config, _mode, ct);

    public OutboxEntry BuildOutboxEntry(
        string clientTaskId,
        DateTime startedAt,
        FinalizeBackupRecordDto finalize,
        Guid? serverRecordId) => new()
    {
        ClientTaskId = clientTaskId,
        DatabaseName = _config.Database,
        ConnectionName = _config.ConnectionName,
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
        BackupMode = _mode,
    };
}
