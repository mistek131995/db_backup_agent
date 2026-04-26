using BackupsterAgent.Configuration;
using BackupsterAgent.Exceptions;

namespace BackupsterAgent.Providers.Restore;

public sealed class MysqlPhysicalRestoreProvider : IRestoreProvider
{
    public Task ValidatePermissionsAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct) =>
        throw new RestorePermissionException(
            "Физический бэкап MySQL не поддерживается. По вопросам реализации обращайтесь: support@backupster.io");

    public Task PrepareTargetDatabaseAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct) =>
        Task.CompletedTask;

    public Task RestoreAsync(ConnectionConfig connection, string targetDatabase, string restoreFilePath, CancellationToken ct) =>
        Task.CompletedTask;
}
