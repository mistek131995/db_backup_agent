using BackupsterAgent.Domain;

namespace BackupsterAgent.Services.Backup.Coordinator;

public sealed record PipelineOutcome
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FilePath { get; init; }
    public long? SizeBytes { get; init; }
    public long? DurationMs { get; init; }
    public string? DumpObjectKey { get; init; }
    public FileBackupMetrics? FileMetrics { get; init; }
    public string? FileBackupError { get; init; }

    public static PipelineOutcome Failed(string errorMessage) =>
        new() { Success = false, ErrorMessage = errorMessage };
}
