namespace DbBackupAgent.Providers;

public sealed class BackupProviderFactory : IBackupProviderFactory
{
    private readonly PostgresBackupProvider _postgres;
    private readonly MssqlBackupProvider _mssql;

    public BackupProviderFactory(PostgresBackupProvider postgres, MssqlBackupProvider mssql)
    {
        _postgres = postgres;
        _mssql = mssql;
    }

    public IBackupProvider GetProvider(string databaseType) => databaseType switch
    {
        "Postgres" => _postgres,
        "Mssql"    => _mssql,
        _ => throw new InvalidOperationException($"Unknown DatabaseType: '{databaseType}'. Supported values: Postgres, Mssql")
    };
}
