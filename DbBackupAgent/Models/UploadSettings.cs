namespace DbBackupAgent.Models;

public sealed class UploadSettings
{
    /// <summary>
    /// Which upload backend to use. Supported values: "S3" (default), "Sftp".
    /// Case-insensitive.
    /// </summary>
    public string Provider { get; set; } = "S3";
}
