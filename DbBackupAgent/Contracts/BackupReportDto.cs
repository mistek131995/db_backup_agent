using DbBackupAgent.Enums;

namespace DbBackupAgent.Contracts;

public sealed class BackupReportDto
{
    public string DatabaseName { get; set; } = string.Empty;
    public BackupStatus Status { get; set; }
    public long SizeBytes { get; set; }
    public long DurationMs { get; set; }
    public string DumpObjectKey { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTime BackupAt { get; set; }

    public string? ManifestKey { get; set; }
    public int? FilesCount { get; set; }
    public long? FilesTotalBytes { get; set; }
    public int? NewChunksCount { get; set; }
    public string? FileBackupError { get; set; }
}
