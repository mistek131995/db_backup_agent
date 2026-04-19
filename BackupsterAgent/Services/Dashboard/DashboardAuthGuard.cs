using Microsoft.Extensions.Hosting;

namespace BackupsterAgent.Services.Dashboard;

public interface IDashboardAuthGuard
{
    void OnUnauthorized(string channel, ILogger logger);
}

public sealed class DashboardAuthGuard : IDashboardAuthGuard
{
    private readonly IHostApplicationLifetime _lifetime;
    private int _triggered;

    public DashboardAuthGuard(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    public void OnUnauthorized(string channel, ILogger logger)
    {
        if (Interlocked.Exchange(ref _triggered, 1) != 0) return;

        logger.LogCritical(
            "Dashboard rejected agent token (HTTP 401) on channel '{Channel}'. " +
            "Agent is shutting down — update AgentSettings:Token and start the service again.",
            channel);

        _lifetime.StopApplication();
    }
}

public sealed class DashboardUnauthorizedException : Exception
{
    public DashboardUnauthorizedException(string channel)
        : base($"Dashboard rejected agent token on channel '{channel}' (HTTP 401).")
    {
    }
}
