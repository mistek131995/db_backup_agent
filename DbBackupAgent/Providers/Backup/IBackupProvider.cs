using DbBackupAgent.Configuration;
using DbBackupAgent.Domain;

namespace DbBackupAgent.Providers;

public interface IBackupProvider
{
    Task<BackupResult> BackupAsync(DatabaseConfig config, ConnectionConfig connection, CancellationToken ct);
}
