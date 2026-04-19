using BackupsterAgent.Enums;

namespace BackupsterAgent.Domain;

public sealed class FileRestoreResult
{
    public required RestoreFilesStatus Status { get; init; }
    public int FilesRestoredCount { get; init; }
    public int FilesFailedCount { get; init; }
    public string? ErrorMessage { get; init; }

    public static FileRestoreResult Skipped() =>
        new() { Status = RestoreFilesStatus.Skipped };

    public static FileRestoreResult Success(int count) =>
        new() { Status = RestoreFilesStatus.Success, FilesRestoredCount = count };

    public static FileRestoreResult Partial(int restored, int failed, string errorMessage) =>
        new()
        {
            Status = RestoreFilesStatus.Partial,
            FilesRestoredCount = restored,
            FilesFailedCount = failed,
            ErrorMessage = errorMessage,
        };

    public static FileRestoreResult Failed(string errorMessage) =>
        new() { Status = RestoreFilesStatus.Failed, ErrorMessage = errorMessage };
}
