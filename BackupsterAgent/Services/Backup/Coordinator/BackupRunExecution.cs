using BackupsterAgent.Enums;
using BackupsterAgent.Services.Common.Progress;

namespace BackupsterAgent.Services.Backup.Coordinator;

public sealed record BackupRunExecution(
    Guid? RecordId,
    bool IsOffline,
    DateTime StartedAt,
    IProgressReporter<BackupStage> Reporter);
