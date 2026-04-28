namespace BackupsterAgent.Configuration;

public sealed class AzureBlobSettings
{
    public string? ConnectionString { get; init; }

    public string? AccountName { get; init; }

    public string? AccountKey { get; init; }

    public string? ServiceUri { get; init; }

    public string ContainerName { get; init; } = string.Empty;
}
