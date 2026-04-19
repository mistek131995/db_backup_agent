namespace BackupsterAgent.Services.Common;

public interface IAgentActivityLock
{
    Task<IDisposable> AcquireAsync(string activityName, CancellationToken ct);
}
