using BackupsterAgent.Enums;

namespace BackupsterAgent.Contracts;

public sealed class BackupTaskPayload
{
    public string DatabaseName { get; init; } = string.Empty;
    public string? FileSetName { get; init; }
    public BackupMode BackupMode { get; init; } = BackupMode.Logical;
}
