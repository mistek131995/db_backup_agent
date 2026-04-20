using BackupsterAgent.Enums;

namespace BackupsterAgent.Contracts;

public sealed class AgentTaskForAgentDto
{
    public Guid Id { get; init; }
    public AgentTaskType Type { get; init; }
    public RestoreTaskPayload? Restore { get; init; }
    public DeleteTaskPayload? Delete { get; init; }
    public BackupTaskPayload? Backup { get; init; }
}
