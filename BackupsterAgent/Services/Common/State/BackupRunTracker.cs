using System.Collections.Concurrent;

namespace BackupsterAgent.Services.Common.State;

public sealed class BackupRunTracker : IBackupRunTracker
{
    private readonly ConcurrentDictionary<string, DateTime> _lastRun;
    private readonly RunStateStore _store;
    private readonly object _writeLock = new();

    public BackupRunTracker(RunStateStore store, ILogger<BackupRunTracker> logger)
    {
        _store = store;
        var initial = _store.LoadAll();
        _lastRun = new ConcurrentDictionary<string, DateTime>(initial, StringComparer.Ordinal);

        if (initial.Count > 0)
        {
            logger.LogInformation(
                "BackupRunTracker: restored last-run state for {Count} run(s) from disk",
                initial.Count);
        }
    }

    public void RecordRun(string key, DateTime whenUtc)
    {
        lock (_writeLock)
        {
            if (_lastRun.TryGetValue(key, out var existing) && existing >= whenUtc)
                return;

            _lastRun[key] = whenUtc;
            _store.Write(key, whenUtc);
        }
    }

    public DateTime? GetLastRun(string key) =>
        _lastRun.TryGetValue(key, out var t) ? t : null;
}
