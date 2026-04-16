using Amazon.S3;
using Amazon.S3.Model;
using DbBackupAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbBackupAgent.Services;

public sealed class S3UploadService : IUploadService, IDisposable
{
    private readonly S3Settings _settings;
    private readonly ILogger<S3UploadService> _logger;
    private AmazonS3Client? _client;

    public S3UploadService(IOptions<S3Settings> settings, ILogger<S3UploadService> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_settings.EndpointUrl))
            _logger.LogWarning("S3Settings:EndpointUrl is not set. S3 uploads will not work until configured.");
    }

    private AmazonS3Client GetClient()
    {
        if (_client is not null)
            return _client;

        if (string.IsNullOrWhiteSpace(_settings.EndpointUrl))
            throw new InvalidOperationException("S3Settings:EndpointUrl is not configured.");

        var config = new AmazonS3Config
        {
            ServiceURL = _settings.EndpointUrl,
            // Path-style addressing is required by MinIO and Yandex Object Storage
            ForcePathStyle = true,
            AuthenticationRegion = _settings.Region,
        };

        _client = new AmazonS3Client(_settings.AccessKey, _settings.SecretKey, config);
        return _client;
    }

    /// <summary>
    /// Uploads <paramref name="filePath"/> to S3 under the key <c>{folder}/{filename}</c>.
    /// </summary>
    /// <returns>Storage path in the form <c>s3://bucket/key</c>.</returns>
    public async Task<string> UploadAsync(string filePath, string folder, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);
        var objectKey = $"{folder.TrimEnd('/')}/{fileName}";

        _logger.LogInformation(
            "Uploading '{FilePath}' → s3://{Bucket}/{ObjectKey}",
            filePath, _settings.BucketName, objectKey);

        await using var fileStream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: true);

        var request = new PutObjectRequest
        {
            BucketName = _settings.BucketName,
            Key = objectKey,
            InputStream = fileStream,
            // Payload signing is not supported by all S3-compatible endpoints (e.g. MinIO, Yandex)
            DisablePayloadSigning = true,
        };

        await GetClient().PutObjectAsync(request, ct);

        var storagePath = $"s3://{_settings.BucketName}/{objectKey}";
        _logger.LogInformation("Upload completed. StoragePath: '{StoragePath}'", storagePath);

        return storagePath;
    }

    public async Task UploadBytesAsync(byte[] content, string objectKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        using var stream = new MemoryStream(content, writable: false);

        var request = new PutObjectRequest
        {
            BucketName = _settings.BucketName,
            Key = objectKey,
            InputStream = stream,
            DisablePayloadSigning = true,
        };
        request.Headers.ContentLength = content.Length;

        await GetClient().PutObjectAsync(request, ct);
    }

    public async Task<bool> ExistsAsync(string objectKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        try
        {
            await GetClient().GetObjectMetadataAsync(_settings.BucketName, objectKey, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public void Dispose() => _client?.Dispose();
}
