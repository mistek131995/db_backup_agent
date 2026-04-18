using DbBackupAgent.Enums;

namespace DbBackupAgent.Configuration;

public sealed class ConnectionConfig
{
    public string Name { get; init; } = string.Empty;
    public DatabaseType DatabaseType { get; init; } = DatabaseType.Postgres;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 5432;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string? SharedBackupPath { get; init; }
    public string? AgentBackupPath { get; init; }
}
