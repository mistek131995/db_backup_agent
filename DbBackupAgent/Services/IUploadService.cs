namespace DbBackupAgent.Services;

public interface IUploadService
{
    /// <summary>
    /// Uploads <paramref name="filePath"/> to the configured storage.
    /// </summary>
    /// <returns>Storage path string identifying where the file was stored.</returns>
    Task<string> UploadAsync(string filePath, string database, CancellationToken ct);
}
