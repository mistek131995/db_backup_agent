namespace DbBackupAgent.Services.Upload;

public interface IUploadService
{
    /// <summary>
    /// Uploads <paramref name="filePath"/> to the configured storage under
    /// <c>{folder}/{filename}</c>. Caller is responsible for building the folder name
    /// (typically <c>{database}/{yyyy-MM-dd_HH-mm-ss}</c>).
    /// </summary>
    /// <param name="progress">Optional byte-level progress sink (cumulative bytes transferred).</param>
    /// <returns>Storage path string identifying where the file was stored.</returns>
    Task<string> UploadAsync(string filePath, string folder, IProgress<long>? progress, CancellationToken ct);

    Task UploadBytesAsync(byte[] content, string objectKey, CancellationToken ct);

    Task<bool> ExistsAsync(string objectKey, CancellationToken ct);

    Task DownloadAsync(string objectKey, string localPath, IProgress<long>? progress, CancellationToken ct);

    Task<byte[]> DownloadBytesAsync(string objectKey, CancellationToken ct);
}
