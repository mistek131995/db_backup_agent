namespace DbBackupAgent.Providers;

public interface IRestoreProviderFactory
{
    IRestoreProvider GetProvider(string databaseType);
}
