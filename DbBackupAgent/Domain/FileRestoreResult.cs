namespace DbBackupAgent.Domain;

public sealed class FileRestoreResult
{
    public required string Status { get; init; }
    public int FilesRestoredCount { get; init; }
    public int FilesFailedCount { get; init; }
    public string? ErrorMessage { get; init; }

    public static FileRestoreResult Skipped() =>
        new() { Status = "skipped" };

    public static FileRestoreResult Success(int count) =>
        new() { Status = "success", FilesRestoredCount = count };

    public static FileRestoreResult Partial(int restored, int failed, string errorMessage) =>
        new()
        {
            Status = "partial",
            FilesRestoredCount = restored,
            FilesFailedCount = failed,
            ErrorMessage = errorMessage,
        };

    public static FileRestoreResult Failed(string errorMessage) =>
        new() { Status = "failed", ErrorMessage = errorMessage };
}
