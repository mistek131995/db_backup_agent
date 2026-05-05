using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BackupsterAgent.Configuration;

namespace BackupsterAgent.Providers.Upload;

public sealed class AzureBlobUploadProvider : IUploadProvider
{
    private readonly AzureBlobSettings _settings;
    private readonly ILogger<AzureBlobUploadProvider> _logger;
    private BlobContainerClient? _client;

    public AzureBlobUploadProvider(AzureBlobSettings settings, ILogger<AzureBlobUploadProvider> logger)
    {
        _settings = settings;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_settings.ContainerName))
            _logger.LogWarning("Azure Blob ContainerName is not set. Uploads will not work until configured.");
    }

    private BlobContainerClient GetClient()
    {
        if (_client is not null)
            return _client;

        if (string.IsNullOrWhiteSpace(_settings.ContainerName))
            throw new InvalidOperationException("Azure Blob ContainerName is not configured.");

        if (!string.IsNullOrWhiteSpace(_settings.ConnectionString))
        {
            _client = new BlobContainerClient(_settings.ConnectionString, _settings.ContainerName);
            return _client;
        }

        if (string.IsNullOrWhiteSpace(_settings.AccountName) ||
            string.IsNullOrWhiteSpace(_settings.AccountKey) ||
            string.IsNullOrWhiteSpace(_settings.ServiceUri))
        {
            throw new InvalidOperationException(
                "Azure Blob storage requires either ConnectionString, or AccountName + AccountKey + ServiceUri.");
        }

        var containerUri = new Uri($"{_settings.ServiceUri.TrimEnd('/')}/{_settings.ContainerName}");
        var credential = new StorageSharedKeyCredential(_settings.AccountName, _settings.AccountKey);

        _client = new BlobContainerClient(containerUri, credential);
        return _client;
    }

    public async Task<string> UploadAsync(string filePath, string folder, IProgress<long>? progress, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);
        var objectKey = $"{folder.TrimEnd('/')}/{fileName}";

        _logger.LogInformation(
            "Uploading '{FilePath}' → azure://{Container}/{ObjectKey}",
            filePath, _settings.ContainerName, objectKey);

        await using var fileStream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: true);

        var options = new BlobUploadOptions();
        if (progress is not null)
            options.ProgressHandler = new Progress<long>(progress.Report);

        var blob = GetClient().GetBlobClient(objectKey);
        await blob.UploadAsync(fileStream, options, ct);

        var storagePath = $"azure://{_settings.ContainerName}/{objectKey}";
        _logger.LogInformation("Upload completed. StoragePath: '{StoragePath}'", storagePath);

        return storagePath;
    }

    public async Task UploadBytesAsync(byte[] content, string objectKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var blob = GetClient().GetBlobClient(objectKey);
        await blob.UploadAsync(BinaryData.FromBytes(content), overwrite: true, ct);
    }

    public async Task<bool> ExistsAsync(string objectKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var blob = GetClient().GetBlobClient(objectKey);
        var response = await blob.ExistsAsync(ct);
        return response.Value;
    }

    public async Task<long> GetObjectSizeAsync(string objectKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var blob = GetClient().GetBlobClient(objectKey);

        try
        {
            var props = await blob.GetPropertiesAsync(cancellationToken: ct);
            return props.Value.ContentLength;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new FileNotFoundException(
                $"Azure blob '{objectKey}' not found in container '{_settings.ContainerName}'.", objectKey);
        }
    }

    public async Task DownloadAsync(string objectKey, string localPath, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        _logger.LogInformation(
            "Downloading azure://{Container}/{ObjectKey} → '{LocalPath}'",
            _settings.ContainerName, objectKey, localPath);

        var blob = GetClient().GetBlobClient(objectKey);
        var tmpPath = localPath + ".download-tmp";

        try
        {
            await using (var blobStream = await OpenReadAsyncOrThrow(blob, objectKey, ct))
            await using (var fileStream = new FileStream(
                tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 65536, useAsync: true))
            {
                if (progress is null)
                {
                    await blobStream.CopyToAsync(fileStream, ct);
                }
                else
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await blobStream.ReadAsync(buffer, ct)) > 0)
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

        var blob = GetClient().GetBlobClient(objectKey);

        try
        {
            var response = await blob.DownloadContentAsync(ct);
            return response.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new FileNotFoundException(
                $"Azure blob '{objectKey}' not found in container '{_settings.ContainerName}'.", objectKey);
        }
    }

    public async IAsyncEnumerable<StorageObject> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var client = GetClient();
        var blobsPrefix = string.IsNullOrEmpty(prefix) ? null : prefix;

        await foreach (var item in client.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.None,
            prefix: blobsPrefix,
            cancellationToken: ct))
        {
            var lastModified = item.Properties.LastModified is { } lm
                ? lm.UtcDateTime
                : DateTime.UtcNow;
            var size = item.Properties.ContentLength ?? 0L;
            yield return new StorageObject(item.Name, lastModified, size);
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var blob = GetClient().GetBlobClient(objectKey);
        await blob.DeleteIfExistsAsync(cancellationToken: ct);
    }

    private async Task<Stream> OpenReadAsyncOrThrow(BlobClient blob, string objectKey, CancellationToken ct)
    {
        try
        {
            return await blob.OpenReadAsync(cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new FileNotFoundException(
                $"Azure blob '{objectKey}' not found in container '{_settings.ContainerName}'.", objectKey);
        }
    }
}
