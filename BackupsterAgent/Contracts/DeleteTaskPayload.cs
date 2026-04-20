namespace BackupsterAgent.Contracts;

public sealed class DeleteTaskPayload
{
    public string StorageName { get; init; } = string.Empty;
    public string? DumpObjectKey { get; init; }
    public string? ManifestKey { get; init; }
}
