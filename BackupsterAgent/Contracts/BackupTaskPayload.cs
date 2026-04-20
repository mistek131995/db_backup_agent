namespace BackupsterAgent.Contracts;

public sealed class BackupTaskPayload
{
    public string DatabaseName { get; init; } = string.Empty;
}
