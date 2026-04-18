using DbBackupAgent.Configuration;

namespace DbBackupAgent.Providers;

public interface IRestoreProvider
{
    Task ValidatePermissionsAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct);

    Task PrepareTargetDatabaseAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct);

    Task RestoreAsync(ConnectionConfig connection, string targetDatabase, string restoreFilePath, CancellationToken ct);
}
