using BackupsterAgent.Enums;
using BackupsterAgent.Settings;

namespace BackupsterAgent.Configuration;

public sealed class StorageConfig
{
    public string Name { get; init; } = string.Empty;
    public UploadProvider Provider { get; init; } = UploadProvider.S3;
    public S3Settings? S3 { get; init; }
    public SftpSettings? Sftp { get; init; }
}
