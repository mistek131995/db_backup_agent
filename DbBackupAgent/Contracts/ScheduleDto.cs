namespace DbBackupAgent.Contracts;

public sealed class ScheduleDto
{
    public string CronExpression { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public List<ScheduleOverrideDto>? Overrides { get; set; }
}
