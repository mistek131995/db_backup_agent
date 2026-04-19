namespace BackupsterAgent.Configuration;

public sealed class DatabaseConfig
{
    public string ConnectionName { get; init; } = string.Empty;
    public string StorageName { get; init; } = string.Empty;
    public string Database { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public List<string> FilePaths { get; init; } = [];
}
