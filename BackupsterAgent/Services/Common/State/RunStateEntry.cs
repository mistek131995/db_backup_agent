namespace BackupsterAgent.Services.Common.State;

internal sealed record RunStateEntry
{
    public string Key { get; init; } = string.Empty;
    public DateTime LastRunUtc { get; init; }
}
