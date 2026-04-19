namespace BackupsterAgent.Contracts;

public sealed class RestoreTaskForAgentDto
{
    public Guid TaskId { get; init; }
    public string SourceDatabaseName { get; init; } = string.Empty;
    public string DumpObjectKey { get; init; } = string.Empty;
    public string? TargetDatabaseName { get; init; }
    public string? ManifestKey { get; init; }
    public string? TargetFileRoot { get; init; }
    public string? TargetConnectionName { get; init; }
    public string? StorageName { get; init; }
}
