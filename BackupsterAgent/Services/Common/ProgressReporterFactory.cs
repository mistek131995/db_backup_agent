using System.Text.Json;
using BackupsterAgent.Contracts;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Dashboard;

namespace BackupsterAgent.Services.Common;

public sealed class ProgressReporterFactory : IProgressReporterFactory
{
    private readonly IAgentTaskClient _taskClient;
    private readonly IBackupRecordClient _backupClient;
    private readonly ILoggerFactory _loggerFactory;

    public ProgressReporterFactory(
        IAgentTaskClient taskClient,
        IBackupRecordClient backupClient,
        ILoggerFactory loggerFactory)
    {
        _taskClient = taskClient;
        _backupClient = backupClient;
        _loggerFactory = loggerFactory;
    }

    public IProgressReporter<RestoreStage> CreateForRestore(Guid taskId)
    {
        var logger = _loggerFactory.CreateLogger<ProgressReporter<RestoreStage>>();
        return new ProgressReporter<RestoreStage>(
            (snap, ct) => _taskClient.ReportProgressAsync(taskId, ToTaskDto(snap), ct),
            logger);
    }

    public IProgressReporter<DeleteStage> CreateForDelete(Guid taskId)
    {
        var logger = _loggerFactory.CreateLogger<ProgressReporter<DeleteStage>>();
        return new ProgressReporter<DeleteStage>(
            (snap, ct) => _taskClient.ReportProgressAsync(taskId, ToTaskDto(snap), ct),
            logger);
    }

    public IProgressReporter<BackupStage> CreateForBackup(Guid backupRecordId, bool offline = false)
    {
        if (offline)
            return new NullProgressReporter<BackupStage>();

        var logger = _loggerFactory.CreateLogger<ProgressReporter<BackupStage>>();
        return new ProgressReporter<BackupStage>(
            (snap, ct) => _backupClient.ReportProgressAsync(backupRecordId, ToDto(snap), ct),
            logger);
    }

    private static AgentTaskProgressDto ToTaskDto<TStage>(ProgressSnapshot<TStage> s)
        where TStage : struct, Enum => new()
    {
        Stage = JsonNamingPolicy.CamelCase.ConvertName(Enum.GetName(s.Stage)!),
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
