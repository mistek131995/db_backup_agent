using BackupsterAgent.Enums;

namespace BackupsterAgent.Contracts;

public sealed class ScheduleOverrideDto
{
    public string DatabaseName { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public BackupMode BackupMode { get; set; } = BackupMode.Logical;
    public List<string>? StorageNames { get; set; }
}
