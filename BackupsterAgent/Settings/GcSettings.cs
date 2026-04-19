namespace BackupsterAgent.Settings;

public sealed class GcSettings
{
    public bool Enabled { get; init; } = true;
    public int IntervalHours { get; init; } = 24;
    public int GraceHours { get; init; } = 24;
}
