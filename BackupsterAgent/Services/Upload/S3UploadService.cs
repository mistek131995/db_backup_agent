using Amazon.S3;
using Amazon.S3.Model;
using BackupsterAgent.Settings;

namespace BackupsterAgent.Services.Upload;

public sealed class S3UploadService : IUploadService, IDisposable
{
    private readonly S3Settings _settings;
    private readonly ILogger<S3UploadService> _logger;
    private AmazonS3Client? _client;

    public S3UploadService(S3Settings settings, ILogger<S3UploadService> logger)
    {
        _settings = settings;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_settings.EndpointUrl))
            _logger.LogWarning("S3 EndpointUrl is not set. Uploads will not work until configured.");
    }

    private AmazonS3Client GetClient()
    {
        if (_client is not null)
            return _client;

        if (string.IsNullOrWhiteSpace(_settings.EndpointUrl))
            throw new InvalidOperationException("S3 EndpointUrl is not configured.");

        var config = new AmazonS3Config
        {
            ServiceURL = _settings.EndpointUrl,
            ForcePathStyle = true,
            AuthenticationRegion = _settings.Region,
        };

        _client = new AmazonS3Client(_settings.AccessKey, _settings.SecretKey, config);
        return _client;
    }

    public async Task<string> UploadAsync(string filePath, string folder, IProgress<long>? progress, CancellationToken ct)
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
            DisablePayloadSigning = true,
        };

        if (progress is not null)
        {
            request.StreamTransferProgress += (_, args) => progress.Report(args.TransferredBytes);
        }

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

    public async Task DownloadAsync(string objectKey, string localPath, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        _logger.LogInformation(
            "Downloading s3://{Bucket}/{ObjectKey} → '{LocalPath}'",
            _settings.BucketName, objectKey, localPath);

        GetObjectResponse response;
        try
        {
            response = await GetClient().GetObjectAsync(_settings.BucketName, objectKey, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException(
                $"S3 object '{objectKey}' not found in bucket '{_settings.BucketName}'.", objectKey);
        }

        var tmpPath = localPath + ".download-tmp";

        try
        {
            using (response)
            await using (var fileStream = new FileStream(
                tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 65536, useAsync: true))
            {
                if (progress is null)
                {
                    await response.ResponseStream.CopyToAsync(fileStream, ct);
                }
                else
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await response.ResponseStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        total += read;
                        progress.Report(total);
                    }
                }
            }

            File.Move(tmpPath, localPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }
            throw;
        }

        _logger.LogInformation("Download completed: '{LocalPath}'", localPath);
    }

    public async Task<byte[]> DownloadBytesAsync(string objectKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        GetObjectResponse response;
        try
        {
            response = await GetClient().GetObjectAsync(_settings.BucketName, objectKey, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException(
                $"S3 object '{objectKey}' not found in bucket '{_settings.BucketName}'.", objectKey);
        }

        using (response)
        {
            using var memory = new MemoryStream();
            await response.ResponseStream.CopyToAsync(memory, ct);
            return memory.ToArray();
        }
    }

    public void Dispose() => _client?.Dispose();
}
