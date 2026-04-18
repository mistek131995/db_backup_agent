namespace DbBackupAgent.Services;

public interface IAgentActivityLock
{
    Task<IDisposable> AcquireAsync(string activityName, CancellationToken ct);
}
