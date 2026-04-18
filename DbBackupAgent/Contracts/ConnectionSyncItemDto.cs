namespace DbBackupAgent.Contracts;

public sealed class ConnectionSyncItemDto
{
    public string Name { get; set; } = string.Empty;
    public string DatabaseType { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
}
