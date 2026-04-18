namespace DbBackupAgent.Domain;

public sealed class DatabaseRestoreResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }

    public static DatabaseRestoreResult Success() =>
        new() { IsSuccess = true };

    public static DatabaseRestoreResult Failed(string errorMessage) =>
        new() { IsSuccess = false, ErrorMessage = errorMessage };
}
