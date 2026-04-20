using BackupsterAgent.Enums;

namespace BackupsterAgent.Contracts;

public sealed class PatchAgentTaskDto
{
    public AgentTaskStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    public RestoreTaskResult? Restore { get; init; }
    public BackupTaskResult? Backup { get; init; }
}
