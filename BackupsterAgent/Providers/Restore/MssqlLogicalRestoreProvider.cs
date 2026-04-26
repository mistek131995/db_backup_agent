using BackupsterAgent.Configuration;
using BackupsterAgent.Exceptions;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace BackupsterAgent.Providers.Restore;

public sealed class MssqlLogicalRestoreProvider(ILogger<MssqlLogicalRestoreProvider> logger) : IRestoreProvider
{
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
        await DropDatabaseAsync(connection, targetDatabase, ct);
        logger.LogInformation("MSSQL logical target database '{Database}' dropped before restore", targetDatabase);
    }

    public async Task RestoreAsync(ConnectionConfig connection, string targetDatabase, string restoreFilePath, CancellationToken ct)
    {
        logger.LogInformation(
            "Starting MSSQL logical restore. Database: '{Database}', Host: '{Host}:{Port}', Source: '{Source}'",
            targetDatabase, connection.Host, connection.Port, restoreFilePath);

        var dac = new DacServices(BuildMasterConnectionString(connection));
        dac.Message += OnDacMessage;
        dac.ProgressChanged += OnDacProgress;

        try
        {
            using var package = BacPackage.Load(restoreFilePath);
            await Task.Run(
                () => dac.ImportBacpac(package, targetDatabase, cancellationToken: ct),
                ct);
        }
        catch (DacServicesException ex)
        {
            logger.LogError(ex, "DacFx ImportBacpac failed for database '{Database}'", targetDatabase);
            throw new InvalidOperationException(
                $"Ошибка импорта MSSQL logical бэкапа в БД '{targetDatabase}': {ex.Message}");
        }
        finally
        {
            dac.Message -= OnDacMessage;
            dac.ProgressChanged -= OnDacProgress;
        }

        logger.LogInformation(
            "MSSQL logical restore completed successfully. Database: '{Database}'", targetDatabase);
    }

    private void OnDacMessage(object? sender, DacMessageEventArgs e)
    {
        var msg = e.Message;
        switch (msg.MessageType)
        {
            case DacMessageType.Error:
                logger.LogError("DacFx Error {Number}: {Message}", msg.Number, msg.Message);
                break;
            case DacMessageType.Warning:
                logger.LogWarning("DacFx Warning {Number}: {Message}", msg.Number, msg.Message);
                break;
            default:
                logger.LogDebug("DacFx Message {Number}: {Message}", msg.Number, msg.Message);
                break;
        }
    }

    private void OnDacProgress(object? sender, DacProgressEventArgs e)
    {
        logger.LogDebug("DacFx Progress: Status={Status}, Message={Message}", e.Status, e.Message);
    }

    private static async Task DropDatabaseAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct)
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
