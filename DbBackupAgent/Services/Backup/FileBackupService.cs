using System.Security.Cryptography;
using DbBackupAgent.Domain;
using Microsoft.Extensions.Logging;

namespace DbBackupAgent.Services;

public sealed record FileBackupResult(FileManifest Manifest, int NewChunksCount);

public sealed class FileBackupService
{
    private readonly ContentDefinedChunker _chunker;
    private readonly EncryptionService _encryption;
    private readonly IUploadServiceFactory _uploadFactory;
    private readonly ILogger<FileBackupService> _logger;
    private bool _consistencyWarningLogged;

    public FileBackupService(
        ContentDefinedChunker chunker,
        EncryptionService encryption,
        IUploadServiceFactory uploadFactory,
        ILogger<FileBackupService> logger)
    {
        _chunker = chunker;
        _encryption = encryption;
        _uploadFactory = uploadFactory;
        _logger = logger;
    }

    public async Task<FileBackupResult> CaptureAsync(List<string> filePaths, CancellationToken ct)
    {
        LogConsistencyWarningOnce();

        var uploader = _uploadFactory.GetService();
        var files = new List<FileEntry>();
        int newChunksTotal = 0;

        var enumOptions = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };

        foreach (var root in filePaths)
        {
            if (!Directory.Exists(root))
            {
                _logger.LogWarning("File path does not exist, skipping: '{Path}'", root);
                continue;
            }

            foreach (var filePath in Directory.EnumerateFiles(root, "*", enumOptions))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var (entry, newChunks) = await CaptureFileAsync(root, filePath, uploader, ct);
                    files.Add(entry);
                    newChunksTotal += newChunks;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Skipping file '{File}': {Reason}", filePath, ex.Message);
                }
            }
        }

        var manifest = new FileManifest(
            CreatedAtUtc: DateTime.UtcNow,
            Database: string.Empty,
            DumpObjectKey: string.Empty,
            Files: files);

        _logger.LogInformation(
            "File backup captured. Files: {FilesCount}, new chunks uploaded: {NewChunks}",
            files.Count, newChunksTotal);

        return new FileBackupResult(manifest, newChunksTotal);
    }

    private async Task<(FileEntry Entry, int NewChunks)> CaptureFileAsync(
        string root, string filePath, IUploadService uploader, CancellationToken ct)
    {
        var info = new FileInfo(filePath);
        var relPath = Path.GetRelativePath(root, filePath).Replace('\\', '/');
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
        var mode = GetUnixMode(filePath);

        var chunks = new List<string>();
        int newChunks = 0;

        await using var fileStream = File.OpenRead(filePath);

        foreach (var chunk in _chunker.Split(fileStream))
        {
            ct.ThrowIfCancellationRequested();

            var shaBytes = SHA256.HashData(chunk);
            var sha = Convert.ToHexString(shaBytes).ToLowerInvariant();
            var objectKey = $"chunks/{sha}";

            if (!await uploader.ExistsAsync(objectKey, ct))
            {
                var encrypted = _encryption.Encrypt(chunk);
                await uploader.UploadBytesAsync(encrypted, objectKey, ct);
                newChunks++;
            }

            chunks.Add(sha);
        }

        var entry = new FileEntry(relPath, info.Length, mtime, mode, chunks);
        return (entry, newChunks);
    }

    private static int GetUnixMode(string filePath)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return 0;

        try { return (int)File.GetUnixFileMode(filePath); }
        catch { return 0; }
    }

    private void LogConsistencyWarningOnce()
    {
        if (_consistencyWarningLogged) return;
        _consistencyWarningLogged = true;
        _logger.LogWarning(
            "File backup reads files without write-locking. For strict point-in-time consistency, " +
            "use filesystem snapshots (LVM/ZFS/btrfs) or application-level pre/post hooks.");
    }
}
