using BackupsterAgent.Enums;

namespace BackupsterAgent.Contracts;

public sealed class RestoreTaskPayload
{
    public string SourceDatabaseName { get; init; } = string.Empty;
    public string? DumpObjectKey { get; init; }
    public string? TargetDatabaseName { get; init; }
    public string? ManifestKey { get; init; }
    public string? TargetFileRoot { get; init; }
    public string? TargetConnectionName { get; init; }
    public string? StorageName { get; init; }
    public BackupMode? BackupMode { get; init; }
}
