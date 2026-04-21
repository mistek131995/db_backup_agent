namespace BackupsterAgent.Domain;

public sealed record OutboxEntry
{
    public int SchemaVersion { get; init; } = 1;
    public required string ClientTaskId { get; init; }
    public required string DatabaseName { get; init; }
    public required string ConnectionName { get; init; }
    public required string StorageName { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime BackupAt { get; init; }
    public required string Status { get; init; }
    public long? SizeBytes { get; init; }
    public long? DurationMs { get; init; }
    public string? DumpObjectKey { get; init; }
    public string? ManifestKey { get; init; }
    public int? FilesCount { get; init; }
    public long? FilesTotalBytes { get; init; }
    public int? NewChunksCount { get; init; }
    public string? FileBackupError { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime QueuedAt { get; init; }
    public int AttemptCount { get; init; }
    public Guid? ServerRecordId { get; init; }
}
