namespace BackupsterAgent.Contracts;

public sealed class OpenBackupRecordDto
{
    public string DatabaseName { get; init; } = string.Empty;
    public string ConnectionName { get; init; } = string.Empty;
}
