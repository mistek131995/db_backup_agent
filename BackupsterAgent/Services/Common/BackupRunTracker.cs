using System.Collections.Concurrent;

namespace BackupsterAgent.Services.Common;

public sealed class BackupRunTracker : IBackupRunTracker
{
    private readonly ConcurrentDictionary<string, DateTime> _lastRun = new(StringComparer.Ordinal);

    public void RecordRun(string databaseName, DateTime whenUtc)
    {
        _lastRun.AddOrUpdate(databaseName, whenUtc,
            (_, existing) => whenUtc > existing ? whenUtc : existing);
    }

    public DateTime? GetLastRun(string databaseName) =>
        _lastRun.TryGetValue(databaseName, out var t) ? t : null;
}
