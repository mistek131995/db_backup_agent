namespace BackupsterAgent.Configuration;

public sealed class WebDavSettings
{
    public string BaseUrl { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string RemotePath { get; init; } = "/";
}
