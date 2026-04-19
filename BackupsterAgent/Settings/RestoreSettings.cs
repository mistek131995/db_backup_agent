namespace BackupsterAgent.Settings;

public sealed class RestoreSettings
{
    public string? TempPath { get; init; }
    public string? FileRestoreBasePath { get; init; }
}
