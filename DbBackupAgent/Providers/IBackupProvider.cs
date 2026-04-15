using DbBackupAgent.Models;

namespace DbBackupAgent.Providers;

public interface IBackupProvider
{
    Task<BackupResult> BackupAsync(DatabaseConfig config, CancellationToken ct);
}
