using BackupsterAgent.Contracts;
using BackupsterAgent.Domain;

namespace BackupsterAgent.Services.Backup.Coordinator;

public interface IBackupRunDescriptor
{
    string DisplayName { get; }
    string ActivityName { get; }
    IReadOnlyList<KeyValuePair<string, string?>> ActivityTags { get; }

    OpenBackupRecordDto BuildOpenDto(DateTime startedAt);

    Task<PipelineOutcome> ExecuteAsync(BackupRunExecution exec, CancellationToken ct);

    OutboxEntry BuildOutboxEntry(
        string clientTaskId,
        DateTime startedAt,
        FinalizeBackupRecordDto finalize,
        Guid? serverRecordId);
}
