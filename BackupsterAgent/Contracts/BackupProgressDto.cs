using BackupsterAgent.Enums;

namespace BackupsterAgent.Contracts;

public sealed class BackupProgressDto
{
    public BackupStage Stage { get; init; }
    public long? Processed { get; init; }
    public long? Total { get; init; }
    public string? Unit { get; init; }
    public string? CurrentItem { get; init; }
}
