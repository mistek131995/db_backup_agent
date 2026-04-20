using BackupsterAgent.Enums;

namespace BackupsterAgent.Contracts;

public sealed class RestoreTaskResult
{
    public RestoreDatabaseStatus? DatabaseStatus { get; init; }
    public RestoreFilesStatus? FilesStatus { get; init; }
    public int? FilesRestoredCount { get; init; }
    public int? FilesFailedCount { get; init; }
}
