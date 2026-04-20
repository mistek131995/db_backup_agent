namespace BackupsterAgent.Services.Common;

public interface IBackupRunTracker
{
    void RecordRun(string databaseName, DateTime whenUtc);

    DateTime? GetLastRun(string databaseName);
}
