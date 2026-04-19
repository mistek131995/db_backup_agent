using BackupsterAgent.Enums;

namespace BackupsterAgent.Contracts;

public sealed class RestoreProgressDto
{
    public RestoreStage Stage { get; init; }
    public long? Processed { get; init; }
    public long? Total { get; init; }
    public string? Unit { get; init; }
    public string? CurrentItem { get; init; }
}
