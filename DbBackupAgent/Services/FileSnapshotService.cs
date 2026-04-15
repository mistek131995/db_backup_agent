using Microsoft.Extensions.Logging;

namespace DbBackupAgent.Services;

public sealed class FileSnapshotService
{
    private readonly ILogger<FileSnapshotService> _logger;

    public FileSnapshotService(ILogger<FileSnapshotService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns all files under <paramref name="path"/>.
    /// If <paramref name="path"/> is a file — returns it directly.
    /// If it is a directory — enumerates recursively.
    /// If it does not exist — logs a warning and returns an empty list.
    /// </summary>
    public IReadOnlyList<string> GetFiles(string path)
    {
        if (File.Exists(path))
            return [path];

        if (Directory.Exists(path))
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList();

        _logger.LogWarning("Snapshot path '{Path}' does not exist, skipping", path);
        return [];
    }
}
