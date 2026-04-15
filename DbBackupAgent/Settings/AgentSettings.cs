namespace DbBackupAgent.Settings;

public sealed class AgentSettings
{
    public string Token { get; init; } = string.Empty;
    public string DashboardUrl { get; init; } = string.Empty;
}
