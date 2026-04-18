using DbBackupAgent.Enums;

namespace DbBackupAgent.Providers;

public sealed class RestoreProviderFactory : IRestoreProviderFactory
{
    private readonly PostgresRestoreProvider _postgres;
    private readonly MssqlRestoreProvider _mssql;
    private readonly MysqlRestoreProvider _mysql;

    public RestoreProviderFactory(
        PostgresRestoreProvider postgres,
        MssqlRestoreProvider mssql,
        MysqlRestoreProvider mysql)
    {
        _postgres = postgres;
        _mssql = mssql;
        _mysql = mysql;
    }

    public IRestoreProvider GetProvider(DatabaseType databaseType) => databaseType switch
    {
        DatabaseType.Postgres => _postgres,
        DatabaseType.Mssql => _mssql,
        DatabaseType.Mysql => _mysql,
        _ => throw new InvalidOperationException($"Unknown DatabaseType: '{databaseType}'. Supported values: Postgres, Mssql, Mysql")
    };
}
