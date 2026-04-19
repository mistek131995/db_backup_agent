using BackupsterAgent.Enums;

namespace BackupsterAgent.Contracts;

public sealed class FinalizeBackupRecordDto
{
    public BackupStatus Status { get; init; }
    public long? SizeBytes { get; init; }
    public long? DurationMs { get; init; }
    public string? DumpObjectKey { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime BackupAt { get; init; }

    public string? ManifestKey { get; init; }
    public int? FilesCount { get; init; }
    public long? FilesTotalBytes { get; init; }
    public int? NewChunksCount { get; init; }
    public string? FileBackupError { get; init; }
}
