using System.Diagnostics;
using BackupsterAgent.Configuration;
using BackupsterAgent.Domain;
using BackupsterAgent.Exceptions;
using BackupsterAgent.Services.Common.Resolvers;
using Microsoft.Data.SqlClient;

namespace BackupsterAgent.Providers.Backup;

public sealed class MssqlPhysicalBackupProvider : IBackupProvider
{
    private readonly ILogger<MssqlPhysicalBackupProvider> _logger;

    public MssqlPhysicalBackupProvider(ILogger<MssqlPhysicalBackupProvider> logger)
    {
        _logger = logger;
    }

    public async Task ValidatePermissionsAsync(ConnectionConfig connection, string database, CancellationToken ct)
    {
        await ValidateBackupPermissionsAsync(connection, database, ct);
        await EnsureNoFilestreamAsync(connection, database, ct);
    }

    private static async Task EnsureNoFilestreamAsync(ConnectionConfig connection, string database, CancellationToken ct)
    {
        const string sql = "SELECT COUNT(*) FROM sys.database_files WHERE type = 2;";

        await using var conn = new SqlConnection(BuildConnectionString(connection, database));
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        var filestreamCount = (int)(await cmd.ExecuteScalarAsync(ct))!;

        if (filestreamCount > 0)
            throw new InvalidOperationException(
                $"Бэкап БД '{database}' содержит FILESTREAM filegroup, что в текущей версии агента не поддерживается. " +
                "Удалите БД из конфига или обратитесь к администратору.");
    }

    private static async Task ValidateBackupPermissionsAsync(ConnectionConfig connection, string database, CancellationToken ct)
    {
        const string sql = @"
SELECT IS_SRVROLEMEMBER('sysadmin')      AS is_sysadmin,
       IS_MEMBER('db_owner')             AS is_owner,
       IS_MEMBER('db_backupoperator')    AS is_backupoperator;";

        await using var conn = new SqlConnection(BuildConnectionString(connection, database));
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            throw new BackupPermissionException("Не удалось прочитать данные о правах пользователя.");

        var isSysadmin       = reader.GetInt32(0) == 1;
        var isOwner          = reader.GetInt32(1) == 1;
        var isBackupOperator = reader.GetInt32(2) == 1;

        if (isSysadmin || isOwner || isBackupOperator) return;

        throw new BackupPermissionException(
            $"Пользователь '{connection.Username}' подключения '{connection.Name}' не имеет прав для physical бэкапа БД '{database}'. " +
            "Требуется членство в server-роли sysadmin, либо в db_owner или db_backupoperator целевой БД. " +
            $"Пример: USE [{database}]; ALTER ROLE db_backupoperator ADD MEMBER [{connection.Username}];");
    }

    public async Task<BackupResult> BackupAsync(DatabaseConfig config, ConnectionConfig connection, CancellationToken ct)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{config.Database}_{timestamp}.bak";

        var sqlDir = await MssqlSharedPathResolver.GetSqlDirAsync(connection, ct);
        var agentDir = await MssqlSharedPathResolver.GetAgentDirAsync(connection, ct);

        Directory.CreateDirectory(agentDir);

        var sqlFilePath = MssqlSharedPathResolver.JoinSqlPath(sqlDir, fileName);
        var agentFilePath = Path.Combine(agentDir, fileName);

        _logger.LogInformation(
            "Starting MSSQL physical backup. Database: '{Database}', Host: '{Host}:{Port}', " +
            "SQL path: '{SqlPath}', Agent path: '{AgentPath}'",
            config.Database, connection.Host, connection.Port, sqlFilePath, agentFilePath);

        var escapedDb = config.Database.Replace("]", "]]");
        var escapedPath = sqlFilePath.Replace("'", "''");
        var tsql = $"BACKUP DATABASE [{escapedDb}] TO DISK = N'{escapedPath}' WITH FORMAT, INIT, STATS = 10;";

        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = $"{connection.Host},{connection.Port}",
            InitialCatalog = "master",
            UserID = connection.Username,
            Password = connection.Password,
            TrustServerCertificate = true,
            Encrypt = true,
        }.ConnectionString;

        var sw = Stopwatch.StartNew();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(tsql, conn) { CommandTimeout = 0 };
        cmd.StatementCompleted += (_, e) =>
            _logger.LogDebug("MSSQL backup progress: {RecordsAffected} rows affected", e.RecordCount);

        await cmd.ExecuteNonQueryAsync(ct);

        sw.Stop();

        if (!File.Exists(agentFilePath))
        {
            throw new InvalidOperationException(
                $"Backup file '{agentFilePath}' is not accessible from the agent host. " +
                "Проверьте, что SharedBackupPath и AgentBackupPath указывают на один и тот же каталог.");
        }

        var sizeBytes = new FileInfo(agentFilePath).Length;

        _logger.LogInformation(
            "MSSQL physical backup completed successfully. File: '{FilePath}', Size: {SizeBytes} bytes, Duration: {DurationMs} ms",
            agentFilePath, sizeBytes, sw.ElapsedMilliseconds);

        return new BackupResult
        {
            FilePath = agentFilePath,
            SizeBytes = sizeBytes,
            DurationMs = sw.ElapsedMilliseconds,
            Success = true,
        };
    }

    private static string BuildConnectionString(ConnectionConfig connection, string database) =>
        new SqlConnectionStringBuilder
        {
            DataSource = $"{connection.Host},{connection.Port}",
            InitialCatalog = database,
            UserID = connection.Username,
            Password = connection.Password,
            TrustServerCertificate = true,
            Encrypt = true,
        }.ToString();
}
