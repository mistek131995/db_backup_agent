using System.Collections.Concurrent;

namespace BackupsterAgent.Services.Common;

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
                "BackupRunTracker: restored last-run state for {Count} database(s) from disk",
                initial.Count);
        }
    }

    public void RecordRun(string databaseName, DateTime whenUtc)
    {
        lock (_writeLock)
        {
            if (_lastRun.TryGetValue(databaseName, out var existing) && existing >= whenUtc)
                return;

            _lastRun[databaseName] = whenUtc;
            _store.Write(databaseName, whenUtc);
        }
    }

    public DateTime? GetLastRun(string databaseName) =>
        _lastRun.TryGetValue(databaseName, out var t) ? t : null;
}
