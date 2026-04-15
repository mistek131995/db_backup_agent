namespace DbBackupAgent.Models;

public sealed class DatabaseConfig
{
    public string DatabaseType { get; init; } = "Postgres";
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 5432;
    public string Database { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public List<string> FilePaths { get; init; } = [];
}
