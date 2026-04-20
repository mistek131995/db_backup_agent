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

internal sealed class NullProgressReporter<T> : IProgressReporter<T>
    where T : struct, Enum
{
    public void Report(T stage, long? processed = null, long? total = null,
                        string? unit = null, string? currentItem = null)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeProgressReporterFactory : IProgressReporterFactory
{
    public IProgressReporter<RestoreStage> CreateForRestore(Guid taskId) =>
        new NullProgressReporter<RestoreStage>();

    public IProgressReporter<DeleteStage> CreateForDelete(Guid taskId) =>
        new NullProgressReporter<DeleteStage>();

    public IProgressReporter<BackupStage> CreateForBackup(Guid backupRecordId) =>
        new NullProgressReporter<BackupStage>();
}

internal sealed class FakeBackupRecordClient : IBackupRecordClient
{
    public Guid? NextId { get; set; } = Guid.NewGuid();
    public int OpenCalls { get; private set; }
    public int ProgressCalls { get; private set; }
    public int FinalizeCalls { get; private set; }
    public FinalizeBackupRecordDto? LastFinalize { get; private set; }

    public Task<Guid?> OpenAsync(OpenBackupRecordDto dto, CancellationToken ct)
    {
        OpenCalls++;
        return Task.FromResult(NextId);
    }

    public Task ReportProgressAsync(Guid backupRecordId, BackupProgressDto progress, CancellationToken ct)
    {
        ProgressCalls++;
        return Task.CompletedTask;
    }

    public Task FinalizeAsync(Guid backupRecordId, FinalizeBackupRecordDto dto, CancellationToken ct)
    {
        FinalizeCalls++;
        LastFinalize = dto;
        return Task.CompletedTask;
    }
}
