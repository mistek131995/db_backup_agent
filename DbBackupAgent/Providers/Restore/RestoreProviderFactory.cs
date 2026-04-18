namespace DbBackupAgent.Providers;

public sealed class RestoreProviderFactory : IRestoreProviderFactory
{
    private readonly PostgresRestoreProvider _postgres;
    private readonly MssqlRestoreProvider _mssql;

    public RestoreProviderFactory(PostgresRestoreProvider postgres, MssqlRestoreProvider mssql)
    {
        _postgres = postgres;
        _mssql = mssql;
    }

    public IRestoreProvider GetProvider(string databaseType) => databaseType switch
    {
        "Postgres" => _postgres,
        "Mssql" => _mssql,
        _ => throw new InvalidOperationException($"Unknown DatabaseType: '{databaseType}'. Supported values: Postgres, Mssql")
    };
}
