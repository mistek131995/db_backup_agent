namespace DbBackupAgent.Contracts;

public sealed class PatchRestoreTaskDto
{
    public string Status { get; init; } = string.Empty;
    public string? DatabaseStatus { get; init; }
    public string? FilesStatus { get; init; }
    public string? ErrorMessage { get; init; }
    public int? FilesRestoredCount { get; init; }
    public int? FilesFailedCount { get; init; }
}
