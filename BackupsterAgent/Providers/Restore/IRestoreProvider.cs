using BackupsterAgent.Configuration;

namespace BackupsterAgent.Providers;

public interface IRestoreProvider
{
    Task ValidatePermissionsAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct);

    Task PrepareTargetDatabaseAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct);

    Task RestoreAsync(ConnectionConfig connection, string targetDatabase, string restoreFilePath, CancellationToken ct);
}
