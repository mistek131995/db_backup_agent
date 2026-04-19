using System.Text;
using BackupsterAgent.Configuration;
using BackupsterAgent.Exceptions;
using BackupsterAgent.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace BackupsterAgent.Providers;

public sealed class MssqlRestoreProvider : IRestoreProvider
{
    private readonly ILogger<MssqlRestoreProvider> _logger;

    public MssqlRestoreProvider(ILogger<MssqlRestoreProvider> logger)
    {
        _logger = logger;
    }

    public async Task ValidatePermissionsAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct)
    {
        const string sql = @"
SELECT IS_SRVROLEMEMBER('sysadmin') AS is_sysadmin,
       IS_SRVROLEMEMBER('dbcreator') AS is_dbcreator;";

        await using var conn = new SqlConnection(BuildMasterConnectionString(connection));
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            throw new RestorePermissionException("Не удалось прочитать данные о ролях пользователя.");

        var isSysadmin = reader.GetInt32(0) == 1;
        var isDbcreator = reader.GetInt32(1) == 1;

        if (isSysadmin || isDbcreator) return;

        throw new RestorePermissionException(
            $"Пользователь '{connection.Username}' подключения '{connection.Name}' не имеет прав для восстановления БД '{targetDatabase}'. " +
            "Требуется членство в server-роли sysadmin или dbcreator. " +
            $"Выдайте права: ALTER SERVER ROLE dbcreator ADD MEMBER [{connection.Username}];.");
    }

    public async Task PrepareTargetDatabaseAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct)
    {
        var quoted = QuoteIdentifier(targetDatabase);
        var escapedName = targetDatabase.Replace("'", "''");

        var sql = $@"
IF DB_ID(N'{escapedName}') IS NOT NULL
BEGIN
    ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE {quoted};
END";

        await using var conn = new SqlConnection(BuildMasterConnectionString(connection));
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("MSSQL target database '{Database}' prepared (dropped if existed)", targetDatabase);
    }

    public async Task RestoreAsync(ConnectionConfig connection, string targetDatabase, string restoreFilePath, CancellationToken ct)
    {
        var fileList = await GetFileListAsync(connection, restoreFilePath, ct);
        var (dataPath, logPath) = await GetDefaultPathsAsync(connection, ct);
        var moveClauses = BuildMoveClauses(fileList, targetDatabase, dataPath, logPath);

        var quoted = QuoteIdentifier(targetDatabase);
        var escapedPath = restoreFilePath.Replace("'", "''");

        var sql = $"RESTORE DATABASE {quoted} FROM DISK = N'{escapedPath}' WITH REPLACE, RECOVERY{moveClauses};";

        _logger.LogInformation(
            "Executing RESTORE DATABASE '{Database}' FROM '{Path}' with {MoveCount} MOVE clause(s)",
            targetDatabase, restoreFilePath, fileList.Count);

        await using var conn = new SqlConnection(BuildMasterConnectionString(connection));
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("MSSQL restore completed for database '{Database}'", targetDatabase);
    }

    private static async Task<List<(string LogicalName, string Type)>> GetFileListAsync(
        ConnectionConfig connection, string restoreFilePath, CancellationToken ct)
    {
        var escapedPath = restoreFilePath.Replace("'", "''");
        var sql = $"RESTORE FILELISTONLY FROM DISK = N'{escapedPath}';";

        await using var conn = new SqlConnection(BuildMasterConnectionString(connection));
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = new List<(string, string)>();
        while (await reader.ReadAsync(ct))
        {
            var logical = reader.GetString(reader.GetOrdinal("LogicalName"));
            var type = reader.GetString(reader.GetOrdinal("Type"));
            result.Add((logical, type));
        }
        return result;
    }

    private static async Task<(string DataPath, string LogPath)> GetDefaultPathsAsync(
        ConnectionConfig connection, CancellationToken ct)
    {
        const string sql = @"
SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(260)) AS DataPath,
       CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(260)) AS LogPath;";

        await using var conn = new SqlConnection(BuildMasterConnectionString(connection));
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Не удалось получить дефолтные пути данных SQL Server.");

        var dataPath = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var logPath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

        if (string.IsNullOrWhiteSpace(dataPath) || string.IsNullOrWhiteSpace(logPath))
        {
            throw new InvalidOperationException(
                "SQL Server не предоставил InstanceDefaultDataPath/InstanceDefaultLogPath. " +
                "Задайте их в инстансе, либо выполните restore на сервере с настроенными дефолтными путями.");
        }

        return (dataPath, logPath);
    }

    private static string BuildMoveClauses(
        List<(string LogicalName, string Type)> fileList, string targetDatabase, string dataPath, string logPath)
    {
        if (fileList.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        var dataIndex = 0;
        var logIndex = 0;

        foreach (var (logical, type) in fileList)
        {
            string newPath;
            if (type == "D")
            {
                var suffix = dataIndex == 0 ? string.Empty : $"_{dataIndex}";
                newPath = CombineSqlPath(dataPath, $"{targetDatabase}{suffix}.mdf");
                dataIndex++;
            }
            else if (type == "L")
            {
                var suffix = logIndex == 0 ? string.Empty : $"_{logIndex}";
                newPath = CombineSqlPath(logPath, $"{targetDatabase}{suffix}.ldf");
                logIndex++;
            }
            else
            {
                continue;
            }

            var escapedLogical = logical.Replace("'", "''");
            var escapedNewPath = newPath.Replace("'", "''");
            sb.Append($", MOVE N'{escapedLogical}' TO N'{escapedNewPath}'");
        }

        return sb.ToString();
    }

    private static string CombineSqlPath(string dir, string fileName)
    {
        var trimmed = dir.TrimEnd('/', '\\');
        var usesBackslash = trimmed.Contains('\\')
            || (trimmed.Length >= 2 && trimmed[1] == ':');
        var sep = usesBackslash ? "\\" : "/";
        return trimmed + sep + fileName;
    }

    private static string BuildMasterConnectionString(ConnectionConfig connection) =>
        new SqlConnectionStringBuilder
        {
            DataSource = $"{connection.Host},{connection.Port}",
            InitialCatalog = "master",
            UserID = connection.Username,
            Password = connection.Password,
            TrustServerCertificate = true,
            Encrypt = true,
        }.ToString();

    private static string QuoteIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Database name cannot be empty", nameof(name));
        return "[" + name.Replace("]", "]]") + "]";
    }
}
