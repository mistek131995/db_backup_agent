using System.Runtime.CompilerServices;
using BackupsterAgent.Configuration;

namespace BackupsterAgent.Providers.Upload;

public sealed class LocalFsUploadProvider : IUploadProvider
{
    private const int CopyBufferSize = 81920;

    private readonly LocalFsSettings _settings;
    private readonly ILogger<LocalFsUploadProvider> _logger;
    private readonly string _basePath;

    public LocalFsUploadProvider(LocalFsSettings settings, ILogger<LocalFsUploadProvider> logger)
    {
        _settings = settings;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_settings.RemotePath))
            throw new InvalidOperationException("LocalFsSettings.RemotePath не задан.");

        try
        {
            _basePath = Path.GetFullPath(_settings.RemotePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"LocalFsSettings.RemotePath '{_settings.RemotePath}' не является корректным путём: {ex.Message}", ex);
        }

        if (File.Exists(_basePath))
            throw new InvalidOperationException(
                $"LocalFsSettings.RemotePath '{_basePath}' указывает на существующий файл, а не на каталог. " +
                "Укажите путь к каталогу для бэкапов.");

        try
        {
            Directory.CreateDirectory(_basePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"Нет прав на создание каталога '{_basePath}'. " +
                "Проверьте, что у пользователя агента есть права на запись по этому пути.", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Не удалось подготовить каталог '{_basePath}': {ex.Message}", ex);
        }
    }

    public async Task<string> UploadAsync(string filePath, string folder, IProgress<long>? progress, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);
        var remoteDir = ResolveLocalPath(folder);
        var remotePath = Path.Combine(remoteDir, fileName);
        EnsureUnderBase(remotePath);

        var fileSize = new FileInfo(filePath).Length;

        _logger.LogInformation(
            "LocalFs uploading '{FilePath}' → '{RemotePath}' ({Size} bytes)",
            filePath, remotePath, fileSize);

        Directory.CreateDirectory(remoteDir);

        var tmpPath = remotePath + ".upload-tmp";

        try
        {
            await using (var src = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true))
            await using (var dst = new FileStream(
                tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
            {
                await CopyWithProgressAsync(src, dst, progress, ct);
                await dst.FlushAsync(ct);
            }

            File.Move(tmpPath, remotePath, overwrite: true);
        }
        catch
        {
            SafeDelete(tmpPath);
            throw;
        }

        var storagePath = $"local://{remotePath.Replace('\\', '/')}";
        _logger.LogInformation("LocalFs upload completed. StoragePath: '{StoragePath}'", storagePath);
        return storagePath;
    }

    public async Task UploadBytesAsync(byte[] content, string objectKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var remotePath = ResolveLocalPath(objectKey);
        EnsureUnderBase(remotePath);

        var remoteDir = Path.GetDirectoryName(remotePath);
        if (!string.IsNullOrEmpty(remoteDir))
            Directory.CreateDirectory(remoteDir);

        var tmpPath = remotePath + ".upload-tmp";

        try
        {
            await using (var dst = new FileStream(
                tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
            {
                await dst.WriteAsync(content.AsMemory(), ct);
                await dst.FlushAsync(ct);
            }

            File.Move(tmpPath, remotePath, overwrite: true);
        }
        catch
        {
            SafeDelete(tmpPath);
            throw;
        }
    }

    public Task<bool> ExistsAsync(string objectKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var remotePath = ResolveLocalPath(objectKey);
        EnsureUnderBase(remotePath);

        return Task.FromResult(File.Exists(remotePath));
    }

    public Task<long> GetObjectSizeAsync(string objectKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var remotePath = ResolveLocalPath(objectKey);
        EnsureUnderBase(remotePath);

        if (!File.Exists(remotePath))
            throw new FileNotFoundException(
                $"Локальный файл '{remotePath}' не найден.", remotePath);

        return Task.FromResult(new FileInfo(remotePath).Length);
    }

    public async Task DownloadAsync(string objectKey, string localPath, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        var remotePath = ResolveLocalPath(objectKey);
        EnsureUnderBase(remotePath);

        if (!File.Exists(remotePath))
            throw new FileNotFoundException(
                $"Локальный файл '{remotePath}' не найден.", remotePath);

        var tmpPath = localPath + ".download-tmp";

        _logger.LogInformation(
            "LocalFs downloading '{RemotePath}' → '{LocalPath}'",
            remotePath, localPath);

        try
        {
            await using (var src = new FileStream(
                remotePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true))
            await using (var dst = new FileStream(
                tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
            {
                await CopyWithProgressAsync(src, dst, progress, ct);
                await dst.FlushAsync(ct);
            }

            File.Move(tmpPath, localPath, overwrite: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            SafeDelete(tmpPath);
            throw new UnauthorizedAccessException(
                $"Нет прав на чтение локального файла '{remotePath}'. " +
                "Проверьте, что у пользователя агента есть права на чтение каталога бэкапов.", ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SafeDelete(tmpPath);
            throw;
        }
        catch
        {
            SafeDelete(tmpPath);
            throw;
        }

        _logger.LogInformation("LocalFs download completed: '{LocalPath}'", localPath);
    }

    public async Task<byte[]> DownloadBytesAsync(string objectKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var remotePath = ResolveLocalPath(objectKey);
        EnsureUnderBase(remotePath);

        if (!File.Exists(remotePath))
            throw new FileNotFoundException(
                $"Локальный файл '{remotePath}' не найден.", remotePath);

        return await File.ReadAllBytesAsync(remotePath, ct);
    }

    public async IAsyncEnumerable<StorageObject> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string searchRoot;
        string keyFilter;

        if (string.IsNullOrEmpty(prefix) || prefix.EndsWith('/'))
        {
            searchRoot = string.IsNullOrEmpty(prefix) ? _basePath : ResolveLocalPath(prefix);
            keyFilter = string.Empty;
        }
        else
        {
            var lastSlash = prefix.LastIndexOf('/');
            searchRoot = lastSlash < 0 ? _basePath : ResolveLocalPath(prefix[..lastSlash]);
            keyFilter = prefix;
        }

        EnsureUnderBase(searchRoot);

        if (!Directory.Exists(searchRoot))
            yield break;

        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };

        foreach (var fullPath in Directory.EnumerateFiles(searchRoot, "*", enumOptions))
        {
            ct.ThrowIfCancellationRequested();

            if (fullPath.EndsWith(".upload-tmp", StringComparison.Ordinal) ||
                fullPath.EndsWith(".download-tmp", StringComparison.Ordinal))
                continue;

            var key = ToObjectKey(fullPath);

            if (keyFilter.Length > 0 && !key.StartsWith(keyFilter, StringComparison.Ordinal))
                continue;

            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                if (!info.Exists) continue;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LocalFs ListAsync: failed to stat '{Path}', skipping", fullPath);
                continue;
            }

            yield return new StorageObject(
                Key: key,
                LastModifiedUtc: info.LastWriteTimeUtc,
                Size: info.Length);
        }
    }

    private string ToObjectKey(string fullPath)
    {
        var relative = Path.GetRelativePath(_basePath, fullPath);
        return relative.Replace('\\', '/');
    }

    public Task DeleteAsync(string objectKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var remotePath = ResolveLocalPath(objectKey);
        EnsureUnderBase(remotePath);

        _logger.LogInformation("LocalFs deleting '{RemotePath}'", remotePath);

        try
        {
            File.Delete(remotePath);
        }
        catch (FileNotFoundException)
        {
            _logger.LogDebug(
                "LocalFs DeleteAsync: '{RemotePath}' not found — treating as already-deleted", remotePath);
        }
        catch (DirectoryNotFoundException)
        {
            _logger.LogDebug(
                "LocalFs DeleteAsync: directory for '{RemotePath}' not found — treating as already-deleted", remotePath);
        }

        TryRemoveEmptyParents(remotePath);

        _logger.LogInformation("LocalFs delete completed. RemotePath: '{RemotePath}'", remotePath);
        return Task.CompletedTask;
    }

    private string ResolveLocalPath(string objectKey)
    {
        var segments = objectKey
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return _basePath;

        var combined = Path.Combine(new[] { _basePath }.Concat(segments).ToArray());
        return Path.GetFullPath(combined);
    }

    private void EnsureUnderBase(string fullPath)
    {
        var basePathWithSep = _basePath.EndsWith(Path.DirectorySeparatorChar)
            ? _basePath
            : _basePath + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(basePathWithSep, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, _basePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Путь '{fullPath}' выходит за пределы корня хранилища '{_basePath}'.");
        }
    }

    private void TryRemoveEmptyParents(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);

        while (!string.IsNullOrEmpty(dir) &&
               !string.Equals(dir, _basePath, StringComparison.OrdinalIgnoreCase) &&
               dir.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(dir)) return;

                if (Directory.EnumerateFileSystemEntries(dir).Any())
                    return;

                Directory.Delete(dir);
                _logger.LogDebug("LocalFs removed empty directory '{Dir}'", dir);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LocalFs keeping '{Dir}' (likely non-empty or no permission)", dir);
                return;
            }

            dir = Path.GetDirectoryName(dir);
        }
    }

    private void SafeDelete(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete LocalFs tmp file '{Path}'", path); }
    }

    private static async Task CopyWithProgressAsync(Stream source, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
            progress?.Report(total);
        }
    }
}
