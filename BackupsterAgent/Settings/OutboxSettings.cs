namespace BackupsterAgent.Settings;

public sealed class OutboxSettings
{
    public int MaxEntries { get; init; } = 1000;
    public int MaxAgeDays { get; init; } = 14;
    public int ReplayIntervalSeconds { get; init; } = 60;
}
