using BackupsterAgent.Enums;

namespace BackupsterAgent.Services.Common;

public interface IProgressReporterFactory
{
    IProgressReporter<RestoreStage> CreateForRestore(Guid taskId);

    IProgressReporter<DeleteStage> CreateForDelete(Guid taskId);

    IProgressReporter<BackupStage> CreateForBackup(Guid backupRecordId);
}
