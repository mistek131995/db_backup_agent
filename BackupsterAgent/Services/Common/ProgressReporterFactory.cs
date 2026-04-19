using BackupsterAgent.Contracts;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Dashboard;

namespace BackupsterAgent.Services.Common;

public sealed class ProgressReporterFactory : IProgressReporterFactory
{
    private readonly IRestoreTaskClient _restoreClient;
    private readonly IBackupRecordClient _backupClient;
    private readonly ILoggerFactory _loggerFactory;

    public ProgressReporterFactory(
        IRestoreTaskClient restoreClient,
        IBackupRecordClient backupClient,
        ILoggerFactory loggerFactory)
    {
        _restoreClient = restoreClient;
        _backupClient = backupClient;
        _loggerFactory = loggerFactory;
    }

    public IProgressReporter<RestoreStage> CreateForRestore(Guid taskId)
    {
        var logger = _loggerFactory.CreateLogger<ProgressReporter<RestoreStage>>();
        return new ProgressReporter<RestoreStage>(
            (snap, ct) => _restoreClient.ReportProgressAsync(taskId, ToDto(snap), ct),
            logger);
    }

    public IProgressReporter<BackupStage> CreateForBackup(Guid backupRecordId)
    {
        var logger = _loggerFactory.CreateLogger<ProgressReporter<BackupStage>>();
        return new ProgressReporter<BackupStage>(
            (snap, ct) => _backupClient.ReportProgressAsync(backupRecordId, ToDto(snap), ct),
            logger);
    }

    private static RestoreProgressDto ToDto(ProgressSnapshot<RestoreStage> s) => new()
    {
        Stage = s.Stage,
        Processed = s.Processed,
        Total = s.Total,
        Unit = s.Unit,
        CurrentItem = s.CurrentItem,
    };

    private static BackupProgressDto ToDto(ProgressSnapshot<BackupStage> s) => new()
    {
        Stage = s.Stage,
        Processed = s.Processed,
        Total = s.Total,
        Unit = s.Unit,
        CurrentItem = s.CurrentItem,
    };
}
