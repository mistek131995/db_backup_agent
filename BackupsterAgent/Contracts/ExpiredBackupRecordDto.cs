namespace BackupsterAgent.Contracts;

public sealed class ExpiredBackupRecordDto
{
    public Guid Id { get; init; }
    public string StorageName { get; init; } = string.Empty;
    public string? DumpObjectKey { get; init; }
    public string? ManifestKey { get; init; }
}
