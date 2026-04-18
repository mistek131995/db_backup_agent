namespace DbBackupAgent.Contracts;

public sealed class ScheduleOverrideDto
{
    public string DatabaseName { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
