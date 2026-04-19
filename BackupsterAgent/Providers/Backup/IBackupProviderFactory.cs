using BackupsterAgent.Enums;

namespace BackupsterAgent.Providers;

public interface IBackupProviderFactory
{
    IBackupProvider GetProvider(DatabaseType databaseType);
}
