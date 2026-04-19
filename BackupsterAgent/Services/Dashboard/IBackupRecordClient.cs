using BackupsterAgent.Contracts;

namespace BackupsterAgent.Services.Dashboard;

public interface IBackupRecordClient
{
    Task<Guid?> OpenAsync(OpenBackupRecordDto dto, CancellationToken ct);

    Task ReportProgressAsync(Guid backupRecordId, BackupProgressDto progress, CancellationToken ct);

    Task FinalizeAsync(Guid backupRecordId, FinalizeBackupRecordDto dto, CancellationToken ct);
}
