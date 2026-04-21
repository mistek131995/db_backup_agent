using BackupsterAgent.Contracts;
using BackupsterAgent.Enums;
using BackupsterAgent.Services;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Dashboard;

namespace BackupsterAgent.Tests;

internal static class TestHelpers
{
    public static IProgressReporter<T> NullReporter<T>() where T : struct, Enum =>
        new NullProgressReporter<T>();
}

internal sealed class FakeProgressReporterFactory : IProgressReporterFactory
{
    public IProgressReporter<RestoreStage> CreateForRestore(Guid taskId) =>
        new NullProgressReporter<RestoreStage>();

    public IProgressReporter<DeleteStage> CreateForDelete(Guid taskId) =>
        new NullProgressReporter<DeleteStage>();

    public IProgressReporter<BackupStage> CreateForBackup(Guid backupRecordId, bool offline = false) =>
        new NullProgressReporter<BackupStage>();
}

internal sealed class FakeBackupRecordClient : IBackupRecordClient
{
    public OpenRecordResult NextOpen { get; set; } = new(DashboardAvailability.Ok, Guid.NewGuid());
    public FinalizeRecordResult NextFinalize { get; set; } = new(DashboardAvailability.Ok);

    public int OpenCalls { get; private set; }
    public int ProgressCalls { get; private set; }
    public int FinalizeCalls { get; private set; }
    public OpenBackupRecordDto? LastOpen { get; private set; }
    public FinalizeBackupRecordDto? LastFinalize { get; private set; }

    public Task<OpenRecordResult> OpenAsync(OpenBackupRecordDto dto, CancellationToken ct)
    {
        OpenCalls++;
        LastOpen = dto;
        return Task.FromResult(NextOpen);
    }

    public Task ReportProgressAsync(Guid backupRecordId, BackupProgressDto progress, CancellationToken ct)
    {
        ProgressCalls++;
        return Task.CompletedTask;
    }

    public Task<FinalizeRecordResult> FinalizeAsync(Guid backupRecordId, FinalizeBackupRecordDto dto, CancellationToken ct)
    {
        FinalizeCalls++;
        LastFinalize = dto;
        return Task.FromResult(NextFinalize);
    }
}
