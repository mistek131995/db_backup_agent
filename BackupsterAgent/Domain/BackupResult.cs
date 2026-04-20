namespace BackupsterAgent.Domain;

public sealed class BackupResult
{
    public string FilePath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public long DurationMs { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? DumpObjectKey { get; init; }
    public Guid? BackupRecordId { get; init; }
}
