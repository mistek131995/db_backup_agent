namespace BackupsterAgent.Services.Common;

public sealed class AgentActivityLock : IAgentActivityLock, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ILogger<AgentActivityLock> _logger;
    private volatile string? _currentHolder;

    public AgentActivityLock(ILogger<AgentActivityLock> logger)
    {
        _logger = logger;
    }

    public async Task<IDisposable> AcquireAsync(string activityName, CancellationToken ct)
    {
        var holder = _currentHolder;
        if (holder is not null)
        {
            _logger.LogInformation(
                "Activity '{Activity}' waiting for lock held by '{Holder}'",
                activityName, holder);
        }

        await _semaphore.WaitAsync(ct);

        _currentHolder = activityName;
        _logger.LogDebug("Activity '{Activity}' acquired lock", activityName);

        return new Releaser(this, activityName);
    }

    private void Release(string activityName)
    {
        _currentHolder = null;
        _semaphore.Release();
        _logger.LogDebug("Activity '{Activity}' released lock", activityName);
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }

    private sealed class Releaser : IDisposable
    {
        private readonly AgentActivityLock _lock;
        private readonly string _activityName;
        private bool _released;

        public Releaser(AgentActivityLock @lock, string activityName)
        {
            _lock = @lock;
            _activityName = activityName;
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            _lock.Release(_activityName);
        }
    }
}
