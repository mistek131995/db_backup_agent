using BackupsterAgent.Enums;

namespace BackupsterAgent.Services.Common.State;

public interface IBackupRunTracker
{
    void RecordRun(string key, DateTime whenUtc);

    DateTime? GetLastRun(string key);

    static string DatabaseKey(string database, BackupMode mode, string storage) =>
        $"db:{database}|{mode}|{storage}";

    static string FileSetKey(string name, string storage) =>
        $"fileset:{name}|{storage}";
}
