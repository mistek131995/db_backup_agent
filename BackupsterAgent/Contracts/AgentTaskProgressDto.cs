namespace BackupsterAgent.Contracts;

public sealed class AgentTaskProgressDto
{
    public string Stage { get; init; } = string.Empty;
    public long? Processed { get; init; }
    public long? Total { get; init; }
    public string? Unit { get; init; }
    public string? CurrentItem { get; init; }
}
