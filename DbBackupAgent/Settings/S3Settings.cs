namespace DbBackupAgent.Settings;

public sealed class S3Settings
{
    /// <summary>Custom endpoint URL — required for MinIO and Yandex Object Storage.</summary>
    public string EndpointUrl { get; init; } = string.Empty;
    public string AccessKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string BucketName { get; init; } = string.Empty;
    public string Region { get; init; } = "us-east-1";
}
