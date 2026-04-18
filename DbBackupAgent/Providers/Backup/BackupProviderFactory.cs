using DbBackupAgent.Enums;

namespace DbBackupAgent.Providers;

public sealed class BackupProviderFactory : IBackupProviderFactory
{
    private readonly PostgresBackupProvider _postgres;
    private readonly MssqlBackupProvider _mssql;
    private readonly MysqlBackupProvider _mysql;

    public BackupProviderFactory(
        PostgresBackupProvider postgres,
        MssqlBackupProvider mssql,
        MysqlBackupProvider mysql)
    {
        _postgres = postgres;
        _mssql = mssql;
        _mysql = mysql;
    }

    public IBackupProvider GetProvider(DatabaseType databaseType) => databaseType switch
    {
        DatabaseType.Postgres => _postgres,
        DatabaseType.Mssql    => _mssql,
        DatabaseType.Mysql    => _mysql,
        _ => throw new InvalidOperationException($"Unknown DatabaseType: '{databaseType}'. Supported values: Postgres, Mssql, Mysql")
    };
}
