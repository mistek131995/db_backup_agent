using BackupsterAgent.Configuration;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Backup.Coordinator;

namespace BackupsterAgent.Services.Backup;

public sealed class BackupJob
{
    private readonly BackupRunCoordinator _coordinator;
    private readonly DatabaseBackupPipeline _pipeline;

    public BackupJob(BackupRunCoordinator coordinator, DatabaseBackupPipeline pipeline)
    {
        _coordinator = coordinator;
        _pipeline = pipeline;
    }

    public Task<BackupResult> RunAsync(DatabaseConfig config, BackupMode mode, CancellationToken ct) =>
        _coordinator.RunAsync(new DatabaseBackupDescriptor(config, mode, _pipeline), ct);
}
