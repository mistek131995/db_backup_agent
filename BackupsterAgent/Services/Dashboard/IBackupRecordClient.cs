using BackupsterAgent.Contracts;

namespace BackupsterAgent.Services.Dashboard;

public interface IBackupRecordClient
{
    Task<OpenRecordResult> OpenAsync(OpenBackupRecordDto dto, CancellationToken ct);

    Task ReportProgressAsync(Guid backupRecordId, BackupProgressDto progress, CancellationToken ct);

    Task<FinalizeRecordResult> FinalizeAsync(Guid backupRecordId, FinalizeBackupRecordDto dto, CancellationToken ct);
}
