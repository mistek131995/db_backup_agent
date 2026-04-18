using DbBackupAgent.Enums;

namespace DbBackupAgent.Settings;

public sealed class UploadSettings
{
    /// <summary>Which upload backend to use. Bound case-insensitive from JSON.</summary>
    public UploadProvider Provider { get; set; } = UploadProvider.S3;
}
