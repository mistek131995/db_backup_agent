namespace BackupsterAgent.Domain;

public sealed record FileBackupMetrics
{
    public required string ManifestKey { get; init; }
    public required int FilesCount { get; init; }
    public required long FilesTotalBytes { get; init; }
    public required int NewChunksCount { get; init; }
}
