using BackupsterAgent.Enums;

namespace BackupsterAgent.Providers;

public interface IRestoreProviderFactory
{
    IRestoreProvider GetProvider(DatabaseType databaseType);
}
