namespace DbBackupAgent.Services;

public interface IUploadService
{
    /// <summary>
    /// Uploads <paramref name="filePath"/> to the configured storage under
    /// <c>{folder}/{filename}</c>. Caller is responsible for building the folder name
    /// (typically <c>{database}_{yyyy-MM-dd_HH-mm-ss}</c>).
    /// </summary>
    /// <returns>Storage path string identifying where the file was stored.</returns>
    Task<string> UploadAsync(string filePath, string folder, CancellationToken ct);

    Task UploadBytesAsync(byte[] content, string objectKey, CancellationToken ct);

    Task<bool> ExistsAsync(string objectKey, CancellationToken ct);
}
