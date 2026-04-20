namespace BackupsterAgent.Domain;

public sealed class BackupDeleteResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }

    public static BackupDeleteResult Success() =>
        new() { IsSuccess = true };

    public static BackupDeleteResult Failed(string errorMessage) =>
        new() { IsSuccess = false, ErrorMessage = errorMessage };
}
