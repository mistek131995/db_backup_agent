using System.Diagnostics;
using BackupsterAgent.Configuration;
using BackupsterAgent.Domain;
using BackupsterAgent.Exceptions;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace BackupsterAgent.Providers.Backup;

public sealed class MssqlLogicalBackupProvider(ILogger<MssqlLogicalBackupProvider> logger) : IBackupProvider
{
    public async Task ValidatePermissionsAsync(ConnectionConfig connection, string database, CancellationToken ct)
    {
        await ValidateBackupPermissionsAsync(connection, database, ct);
    }

    private static async Task ValidateBackupPermissionsAsync(ConnectionConfig connection, string database, CancellationToken ct)
    {
        const string sql = @"
SELECT IS_MEMBER('db_owner')     AS is_owner,
       IS_MEMBER('db_datareader') AS is_datareader,
       HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')      AS can_view_def,
       HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DATABASE STATE')  AS can_view_state;";

        await using var conn = new SqlConnection(BuildConnectionString(connection, database));
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            throw new BackupPermissionException("Не удалось прочитать данные о правах пользователя.");

        var isOwner      = reader.GetInt32(0) == 1;
        var isDatareader = reader.GetInt32(1) == 1;
        var canViewDef   = reader.GetInt32(2) == 1;
        var canViewState = reader.GetInt32(3) == 1;

        if (isOwner || (isDatareader && canViewDef && canViewState)) return;

        throw new BackupPermissionException(
            $"Пользователь '{connection.Username}' подключения '{connection.Name}' не имеет прав для logical бэкапа БД '{database}'. " +
            "Требуется членство в db_owner, либо одновременно: db_datareader + VIEW DEFINITION + VIEW DATABASE STATE. " +
            $"Пример: ALTER ROLE db_datareader ADD MEMBER [{connection.Username}]; " +
            $"GRANT VIEW DEFINITION TO [{connection.Username}]; " +
            $"GRANT VIEW DATABASE STATE TO [{connection.Username}];");
    }

    public async Task<BackupResult> BackupAsync(DatabaseConfig config, ConnectionConfig connection, CancellationToken ct)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{config.Database}_{timestamp}.bacpac";
        var outputFile = Path.Combine(config.OutputPath, fileName);

        Directory.CreateDirectory(config.OutputPath);

        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = $"{connection.Host},{connection.Port}",
            InitialCatalog = config.Database,
            UserID = connection.Username,
            Password = connection.Password,
            Encrypt = true,
            TrustServerCertificate = true,
        }.ConnectionString;

        logger.LogInformation(
            "Starting MSSQL logical backup. Database: '{Database}', Host: '{Host}:{Port}', Output: '{Output}'",
            config.Database, connection.Host, connection.Port, outputFile);

        var dac = new DacServices(connectionString);
        dac.Message += OnDacMessage;
        dac.ProgressChanged += OnDacProgress;

        var sw = Stopwatch.StartNew();
        try
        {
            await Task.Run(
                () => dac.ExportBacpac(outputFile, config.Database, cancellationToken: ct),
                ct);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(outputFile);
            throw;
        }
        catch (DacServicesException ex)
        {
            TryDeleteFile(outputFile);
            logger.LogError(ex, "DacFx ExportBacpac failed for database '{Database}'", config.Database);
            throw new InvalidOperationException(
                $"Ошибка экспорта MSSQL logical бэкапа БД '{config.Database}': {ex.Message}");
        }
        finally
        {
            dac.Message -= OnDacMessage;
            dac.ProgressChanged -= OnDacProgress;
        }

        sw.Stop();

        if (!File.Exists(outputFile))
        {
            throw new InvalidOperationException(
                $"Файл bacpac '{outputFile}' не создан DacFx, хотя операция завершилась без ошибок.");
        }

        var sizeBytes = new FileInfo(outputFile).Length;

        logger.LogInformation(
            "MSSQL logical backup completed successfully. File: '{FilePath}', Size: {SizeBytes} bytes, Duration: {DurationMs} ms",
            outputFile, sizeBytes, sw.ElapsedMilliseconds);

        return new BackupResult
        {
            FilePath = outputFile,
            SizeBytes = sizeBytes,
            DurationMs = sw.ElapsedMilliseconds,
            Success = true,
        };
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

    private void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not delete partial file '{Path}'", path); }
    }

    private static string BuildConnectionString(ConnectionConfig connection, string database) =>
        new SqlConnectionStringBuilder
        {
            DataSource = $"{connection.Host},{connection.Port}",
            InitialCatalog = database,
            UserID = connection.Username,
            Password = connection.Password,
            Encrypt = true,
            TrustServerCertificate = true,
        }.ConnectionString;
}
