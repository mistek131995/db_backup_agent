namespace BackupsterAgent.Settings;

public sealed class RetentionSettings
{
    public bool Enabled { get; init; } = true;
    public int IntervalHours { get; init; } = 6;
    public int BatchSize { get; init; } = 50;
}
