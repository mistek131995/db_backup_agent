using DbBackupAgent.Enums;

namespace DbBackupAgent.Providers;

public interface IRestoreProviderFactory
{
    IRestoreProvider GetProvider(DatabaseType databaseType);
}
