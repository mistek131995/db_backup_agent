using DbBackupAgent.Enums;

namespace DbBackupAgent.Providers;

public interface IBackupProviderFactory
{
    IBackupProvider GetProvider(DatabaseType databaseType);
}
