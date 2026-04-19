using BackupsterAgent.Enums;

namespace BackupsterAgent.Contracts;

public sealed class PatchRestoreTaskDto
{
    public RestoreTaskStatus Status { get; init; }
    public RestoreDatabaseStatus? DatabaseStatus { get; init; }
    public RestoreFilesStatus? FilesStatus { get; init; }
    public string? ErrorMessage { get; init; }
    public int? FilesRestoredCount { get; init; }
    public int? FilesFailedCount { get; init; }
}
