namespace DbBackupAgent.Providers;

public interface IBackupProviderFactory
{
    IBackupProvider GetProvider(string databaseType);
}
