namespace DbBackupAgent.Models;

public sealed class ScheduleDto
{
    public string CronExpression { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
