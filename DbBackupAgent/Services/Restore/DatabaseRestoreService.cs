using System.IO.Compression;
using System.Security.Cryptography;
using DbBackupAgent.Configuration;
using DbBackupAgent.Contracts;
using DbBackupAgent.Domain;
using DbBackupAgent.Enums;
using DbBackupAgent.Exceptions;
using DbBackupAgent.Providers;
using DbBackupAgent.Services.Common;
using DbBackupAgent.Services.Upload;
using DbBackupAgent.Settings;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;

namespace DbBackupAgent.Services.Restore;

public sealed class DatabaseRestoreService
{
    private static readonly int[] MssqlPermissionErrorCodes = { 229, 262, 300, 916, 15247, 21089 };

    private readonly ConnectionResolver _connections;
    private readonly IRestoreProviderFactory _restoreFactory;
    private readonly EncryptionService _encryption;
    private readonly RestoreSettings _restoreSettings;
    private readonly List<DatabaseConfig> _databases;
    private readonly ILogger<DatabaseRestoreService> _logger;

    public DatabaseRestoreService(
        ConnectionResolver connections,
        IRestoreProviderFactory restoreFactory,
        EncryptionService encryption,
        IOptions<RestoreSettings> restoreSettings,
        IOptions<List<DatabaseConfig>> databases,
        ILogger<DatabaseRestoreService> logger)
    {
        _connections = connections;
        _restoreFactory = restoreFactory;
        _encryption = encryption;
        _restoreSettings = restoreSettings.Value;
        _databases = databases.Value;
        _logger = logger;
    }

    public async Task<DatabaseRestoreResult> RunAsync(
        RestoreTaskForAgentDto task,
        IUploadService uploader,
        IProgressReporter<RestoreStage> reporter,
        CancellationToken ct)
    {
        var tempDir = ResolveTempDir(task.TaskId);
        var targetDatabase = string.IsNullOrWhiteSpace(task.TargetDatabaseName)
            ? task.SourceDatabaseName
            : task.TargetDatabaseName!;

        string? mssqlBakAgentPath = null;

        try
        {
            var connection = ResolveTargetConnection(task);
            var provider = _restoreFactory.GetProvider(connection.DatabaseType);

            _logger.LogInformation(
                "DatabaseRestoreService starting. Task: {TaskId}, Target: '{Target}' on connection '{Connection}' ({Type})",
                task.TaskId, targetDatabase, connection.Name, connection.DatabaseType);

            Directory.CreateDirectory(tempDir);

            await provider.ValidatePermissionsAsync(connection, targetDatabase, ct);

            var encryptedPath = Path.Combine(tempDir, "dump.enc");
            reporter.Report(RestoreStage.DownloadingDump, processed: 0, unit: "bytes");
            var downloadProgress = new Progress<long>(bytes =>
                reporter.Report(RestoreStage.DownloadingDump, processed: bytes, unit: "bytes"));
            await uploader.DownloadAsync(task.DumpObjectKey, encryptedPath, downloadProgress, ct);

            var decryptedPath = Path.Combine(tempDir, "dump.bin");
            reporter.Report(RestoreStage.DecryptingDump);
            await _encryption.DecryptAsync(encryptedPath, decryptedPath, ct);
            SafeDelete(encryptedPath);

            string restoreFilePath;
            if (connection.DatabaseType is DatabaseType.Postgres or DatabaseType.Mysql)
            {
                var sqlPath = Path.Combine(tempDir, "dump.sql");
                reporter.Report(RestoreStage.DecompressingDump);
                await DecompressGzipAsync(decryptedPath, sqlPath, ct);
                SafeDelete(decryptedPath);
                restoreFilePath = sqlPath;
            }
            else if (connection.DatabaseType == DatabaseType.Mssql)
            {
                var fileName = $"{targetDatabase}_{task.TaskId:N}.bak";
                var sqlDir = MssqlSharedPathResolver.GetSqlDir(connection, tempDir);
                var agentDir = MssqlSharedPathResolver.GetAgentDir(connection, tempDir);
                Directory.CreateDirectory(agentDir);

                mssqlBakAgentPath = Path.Combine(agentDir, fileName);
                File.Copy(decryptedPath, mssqlBakAgentPath, overwrite: true);
                SafeDelete(decryptedPath);

                restoreFilePath = MssqlSharedPathResolver.JoinSqlPath(sqlDir, fileName);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unsupported DatabaseType: '{connection.DatabaseType}'. Supported: Postgres, Mssql, Mysql.");
            }

            reporter.Report(RestoreStage.PreparingDatabase);
            await provider.PrepareTargetDatabaseAsync(connection, targetDatabase, ct);

            reporter.Report(RestoreStage.RestoringDatabase);
            await provider.RestoreAsync(connection, targetDatabase, restoreFilePath, ct);

            _logger.LogInformation(
                "DatabaseRestoreService completed successfully. Task: {TaskId}, Target: '{Target}'",
                task.TaskId, targetDatabase);

            return DatabaseRestoreResult.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RestorePermissionException ex)
        {
            _logger.LogError(ex, "DatabaseRestoreService: permission check failed for task {TaskId}", task.TaskId);
            return DatabaseRestoreResult.Failed(ex.Message);
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            _logger.LogError(ex, "DatabaseRestoreService: Postgres permission denied for task {TaskId}", task.TaskId);
            return DatabaseRestoreResult.Failed(
                $"Недостаточно прав у пользователя Postgres при выполнении restore БД '{targetDatabase}'. " +
                "Требуются: роль CREATEDB и pg_signal_backend, либо superuser.");
        }
        catch (SqlException ex) when (IsMssqlPermissionError(ex))
        {
            _logger.LogError(ex, "DatabaseRestoreService: MSSQL permission denied for task {TaskId}", task.TaskId);
            return DatabaseRestoreResult.Failed(
                $"Недостаточно прав у пользователя MSSQL при выполнении restore БД '{targetDatabase}'. " +
                "Требуется членство в server-роли sysadmin или dbcreator.");
        }
        catch (MySqlException ex) when (IsMysqlPermissionError(ex))
        {
            _logger.LogError(ex, "DatabaseRestoreService: MySQL permission denied for task {TaskId}", task.TaskId);
            return DatabaseRestoreResult.Failed(
                $"Недостаточно прав у пользователя MySQL при выполнении restore БД '{targetDatabase}'. " +
                $"Требуются привилегии CREATE и DROP на БД либо глобально (ON *.*).");
        }
        catch (AuthenticationTagMismatchException ex)
        {
            _logger.LogError(ex, "DatabaseRestoreService: decrypt auth tag mismatch for task {TaskId}", task.TaskId);
            return DatabaseRestoreResult.Failed(
                $"Не удалось расшифровать дамп БД '{task.SourceDatabaseName}'. " +
                "Вероятные причины: EncryptionKey агента изменился после создания бэкапа, ключ отличается " +
                "от того, что был на момент бэкапа, или файл повреждён в хранилище.");
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "DatabaseRestoreService: cryptographic error for task {TaskId}", task.TaskId);
            return DatabaseRestoreResult.Failed(
                $"Ошибка криптографии при расшифровке дампа БД '{task.SourceDatabaseName}'. " +
                "Проверьте EncryptionKey агента и целостность файла в хранилище.");
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("Bad magic", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(ex, "DatabaseRestoreService: unknown file format for task {TaskId}", task.TaskId);
            return DatabaseRestoreResult.Failed(
                $"Формат зашифрованного файла '{task.DumpObjectKey}' не поддерживается этой версией агента. " +
                "Обновите агент или используйте версию, которой был создан бэкап.");
        }
        catch (InvalidDataException ex)
        {
            _logger.LogError(ex, "DatabaseRestoreService: truncated or invalid file for task {TaskId}", task.TaskId);
            return DatabaseRestoreResult.Failed(
                $"Зашифрованный файл '{task.DumpObjectKey}' повреждён или усечён. Проверьте целостность в хранилище.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DatabaseRestoreService: unexpected error for task {TaskId}", task.TaskId);
            return DatabaseRestoreResult.Failed($"Ошибка восстановления БД: {ex.Message}");
        }
        finally
        {
            if (mssqlBakAgentPath is not null)
                SafeDelete(mssqlBakAgentPath);

            TryDeleteDirectory(tempDir);
        }
    }

    internal ConnectionConfig ResolveTargetConnection(RestoreTaskForAgentDto task)
    {
        if (!string.IsNullOrWhiteSpace(task.TargetConnectionName))
            return _connections.Resolve(task.TargetConnectionName);

        var dbConfig = _databases.FirstOrDefault(
            d => string.Equals(d.Database, task.SourceDatabaseName, StringComparison.Ordinal));

        if (dbConfig is null)
        {
            throw new InvalidOperationException(
                $"БД '{task.SourceDatabaseName}' не найдена в конфиге агента. " +
                "Укажите target-подключение явно через TargetConnectionName.");
        }

        return _connections.Resolve(dbConfig.ConnectionName);
    }

    internal string ResolveTempDir(Guid taskId) =>
        BuildTempDir(_restoreSettings.TempPath, taskId);

    internal static string BuildTempDir(string? tempPath, Guid taskId)
    {
        var raw = string.IsNullOrWhiteSpace(tempPath) ? "./temp" : tempPath;

        var absolute = Path.IsPathRooted(raw)
            ? raw
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, raw));

        return Path.Combine(absolute, taskId.ToString("N"));
    }

    private static async Task DecompressGzipAsync(string sourcePath, string destPath, CancellationToken ct)
    {
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: true);
        await using var gz = new GZipStream(source, CompressionMode.Decompress);
        await using var dest = new FileStream(
            destPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 65536, useAsync: true);
        await gz.CopyToAsync(dest, ct);
    }

    private static bool IsMssqlPermissionError(SqlException ex)
    {
        foreach (SqlError error in ex.Errors)
        {
            if (IsKnownMssqlPermissionCode(error.Number))
                return true;
        }
        return false;
    }

    internal static bool IsKnownMssqlPermissionCode(int errorNumber) =>
        Array.IndexOf(MssqlPermissionErrorCodes, errorNumber) >= 0;

    private static bool IsMysqlPermissionError(MySqlException ex) =>
        ex.Number is 1044 or 1045 or 1142 or 1143 or 1227;

    private void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete file '{Path}'", path);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete directory '{Path}'", path);
        }
    }
}
