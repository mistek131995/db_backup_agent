namespace DbBackupAgent.Models;

public sealed class BackupResult
{
    public string FilePath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public long DurationMs { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>S3 storage path, e.g. <c>s3://bucket/db/2026-04-14/file.sql.gz.enc</c>.</summary>
    public string? StoragePath { get; init; }
}
