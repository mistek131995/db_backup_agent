namespace BackupsterAgent.Configuration;

public sealed class RestoreSettings
{
    public string? TempPath { get; init; }
    public string? FileRestoreBasePath { get; init; }
    public int PgCtlStartTimeoutSeconds { get; init; } = 600;
}
