using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using BackupsterAgent.Configuration;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace BackupsterAgent.Providers.Upload;

public sealed class SftpUploadProvider : IUploadProvider, IAsyncDisposable
{
    private readonly SftpSettings _settings;
    private readonly ILogger<SftpUploadProvider> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private SftpClient? _client;
    private bool _disposed;
    private int _warnedUntrusted;

    public SftpUploadProvider(SftpSettings settings, ILogger<SftpUploadProvider> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    internal static string ComputeFingerprint(byte[] hostKey) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');

    public async Task<string> UploadAsync(string filePath, string folder, IProgress<long>? progress, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);
        var remoteDir = $"{_settings.RemotePath.TrimEnd('/')}/{folder.TrimEnd('/')}";
        var remotePath = $"{remoteDir}/{fileName}";

        _logger.LogInformation(
            "SFTP uploading '{FilePath}' → {Host}:{RemotePath}",
            filePath, _settings.Host, remotePath);

        await ExecuteAsync((client, token) =>
        {
            EnsureRemoteDirectory(client, remoteDir, token);

            using var fileStream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 65536);

            Action<ulong>? callback = progress is null
                ? null
                : uploaded => progress.Report((long)uploaded);

            return Task.Run(() =>
            {
                using var reg = token.Register(() =>
                {
                    try { client.Disconnect(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "SFTP disconnect on cancel failed (best-effort)"); }
                });

                client.UploadFile(fileStream, remotePath, canOverride: true, uploadCallback: callback);
            }, token);
        }, ct);

        var storagePath = $"sftp://{_settings.Host}{remotePath}";
        _logger.LogInformation("SFTP upload completed. StoragePath: '{StoragePath}'", storagePath);

        return storagePath;
    }

    public async Task UploadBytesAsync(byte[] content, string objectKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var remotePath = ResolveRemotePath(objectKey);
        var remoteDir = GetParentPath(remotePath);

        await ExecuteAsync((client, token) =>
        {
            return Task.Run(() =>
            {
                if (!string.IsNullOrEmpty(remoteDir))
                    EnsureRemoteDirectory(client, remoteDir, token);

                using var ms = new MemoryStream(content, writable: false);
                client.UploadFile(ms, remotePath, canOverride: true);
            }, token);
        }, ct);
    }

    public async Task<bool> ExistsAsync(string objectKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var remotePath = ResolveRemotePath(objectKey);

        return await ExecuteAsync((client, token) =>
            Task.Run(() => client.Exists(remotePath), token), ct);
    }

    public async Task<long> GetObjectSizeAsync(string objectKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var remotePath = ResolveRemotePath(objectKey);

        return await ExecuteAsync<long>((client, _) =>
        {
            try
            {
                var attrs = client.GetAttributes(remotePath);
                return Task.FromResult(attrs.Size);
            }
            catch (SftpPathNotFoundException)
            {
                throw new FileNotFoundException(
                    $"SFTP-файл '{remotePath}' не найден на хосте {_settings.Host}.", remotePath);
            }
        }, ct);
    }

    public async Task DownloadAsync(string objectKey, string localPath, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        var remotePath = ResolveRemotePath(objectKey);
        var tmpPath = localPath + ".download-tmp";

        _logger.LogInformation(
            "SFTP downloading {Host}:{RemotePath} → '{LocalPath}'",
            _settings.Host, remotePath, localPath);

        try
        {
            await ExecuteAsync((client, token) =>
            {
                return Task.Run(() =>
                {
                    using var fileStream = new FileStream(
                        tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        bufferSize: 65536);

                    Action<ulong>? callback = progress is null
                        ? null
                        : downloaded => progress.Report((long)downloaded);

                    using var reg = token.Register(() =>
                    {
                        try { client.Disconnect(); }
                        catch (Exception ex) { _logger.LogDebug(ex, "SFTP disconnect on cancel failed (best-effort)"); }
                    });

                    client.DownloadFile(remotePath, fileStream, callback);
                }, token);
            }, ct);

            File.Move(tmpPath, localPath, overwrite: true);
        }
        catch (SftpPathNotFoundException)
        {
            SafeDeleteTmp(tmpPath);
            throw new FileNotFoundException(
                $"SFTP-файл '{remotePath}' не найден на хосте {_settings.Host}.", remotePath);
        }
        catch (SftpPermissionDeniedException ex)
        {
            SafeDeleteTmp(tmpPath);
            throw new UnauthorizedAccessException(
                $"Нет прав на чтение SFTP-файла '{remotePath}' на хосте {_settings.Host}. " +
                "Проверьте, что у SSH-пользователя есть права на чтение файла бэкапа.", ex);
        }
        catch (SshAuthenticationException ex)
        {
            SafeDeleteTmp(tmpPath);
            throw new UnauthorizedAccessException(
                $"Не удалось аутентифицироваться на SFTP-сервере {_settings.Host}. " +
                "Проверьте логин/пароль или приватный ключ в настройках хранилища.", ex);
        }
        catch (SshConnectionException ex)
        {
            SafeDeleteTmp(tmpPath);
            throw new IOException(
                $"Не удалось подключиться к SFTP-серверу {_settings.Host}:{_settings.Port}. " +
                "Проверьте сетевой доступ и host key fingerprint.", ex);
        }
        catch (OperationCanceledException)
        {
            SafeDeleteTmp(tmpPath);
            throw;
        }
        catch
        {
            SafeDeleteTmp(tmpPath);
            throw;
        }

        _logger.LogInformation("SFTP download completed: '{LocalPath}'", localPath);
    }

    public async Task<byte[]> DownloadBytesAsync(string objectKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var remotePath = ResolveRemotePath(objectKey);

        return await ExecuteAsync((client, token) =>
        {
            return Task.Run(() =>
            {
                using var ms = new MemoryStream();
                try
                {
                    client.DownloadFile(remotePath, ms);
                }
                catch (SftpPathNotFoundException)
                {
                    throw new FileNotFoundException(
                        $"SFTP-файл '{remotePath}' не найден на хосте {_settings.Host}.", remotePath);
                }
                return ms.ToArray();
            }, token);
        }, ct);
    }

    public async IAsyncEnumerable<StorageObject> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var baseDir = _settings.RemotePath.TrimEnd('/');
        var startDir = string.IsNullOrEmpty(prefix)
            ? baseDir
            : $"{baseDir}/{prefix.Trim('/')}";

        var queue = new Queue<string>();
        queue.Enqueue(startDir);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = queue.Dequeue();

            List<ISftpFile>? entries;
            try
            {
                entries = await ExecuteAsync((client, token) =>
                    Task.Run(() => client.ListDirectory(dir).ToList(), token), ct);
            }
            catch (SftpPathNotFoundException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.Name is "." or "..") continue;

                if (entry.IsDirectory)
                {
                    queue.Enqueue(entry.FullName);
                    continue;
                }

                if (!entry.IsRegularFile) continue;

                var fullName = entry.FullName;
                var key = fullName.StartsWith(baseDir, StringComparison.Ordinal)
                    ? fullName[baseDir.Length..].TrimStart('/')
                    : fullName.TrimStart('/');

                var lastWriteUtc = DateTime.SpecifyKind(
                    entry.LastWriteTime.ToUniversalTime(),
                    DateTimeKind.Utc);

                yield return new StorageObject(key, lastWriteUtc, entry.Length);
            }
        }
    }

    private void SafeDeleteTmp(string tmpPath)
    {
        if (!File.Exists(tmpPath)) return;
        try { File.Delete(tmpPath); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete SFTP download tmp file '{TmpPath}'", tmpPath); }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken ct)
    {
        var baseDir = _settings.RemotePath.TrimEnd('/');
        var remotePath = ResolveRemotePath(objectKey);

        _logger.LogInformation(
            "SFTP deleting {Host}:{RemotePath}", _settings.Host, remotePath);

        await ExecuteAsync((client, token) =>
        {
            try
            {
                client.DeleteFile(remotePath);
            }
            catch (SftpPathNotFoundException)
            {
                _logger.LogDebug(
                    "SFTP DeleteAsync: {RemotePath} not found — treating as already-deleted", remotePath);
            }

            TryRemoveEmptyParents(client, remotePath, baseDir, token);
            return Task.CompletedTask;
        }, ct);

        _logger.LogInformation("SFTP delete completed. RemotePath: '{RemotePath}'", remotePath);
    }

    private string ResolveRemotePath(string objectKey) =>
        $"{_settings.RemotePath.TrimEnd('/')}/{objectKey.TrimStart('/')}";

    private async Task<T> ExecuteAsync<T>(Func<SftpClient, CancellationToken, Task<T>> op, CancellationToken ct)
    {
        ThrowIfDisposed();

        await _semaphore.WaitAsync(ct);
        try
        {
            var client = await EnsureConnectedAsync(ct);
            try
            {
                return await op(client, ct);
            }
            catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
            {
                throw new OperationCanceledException("SFTP operation cancelled by stoppingToken.", ex, ct);
            }
            catch (Exception ex) when (ex is SshConnectionException or SocketException)
            {
                _logger.LogWarning(ex, "SFTP connection lost during operation on {Host}:{Port}; retrying once.",
                    _settings.Host, _settings.Port);
                DisposeClientLocked();
                client = await EnsureConnectedAsync(ct);
                return await op(client, ct);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private Task ExecuteAsync(Func<SftpClient, CancellationToken, Task> op, CancellationToken ct) =>
        ExecuteAsync<object?>(async (client, token) =>
        {
            await op(client, token);
            return null;
        }, ct);

    private async Task<SftpClient> EnsureConnectedAsync(CancellationToken ct)
    {
        ThrowIfDisposed();

        if (_client is { IsConnected: true })
            return _client;

        if (_client is not null)
        {
            DisposeClientLocked();
        }

        var client = BuildClient();
        try
        {
            await Task.Run(() => client.Connect(), ct);
        }
        catch
        {
            try { client.Dispose(); }
            catch { /* swallow */ }
            throw;
        }

        _client = client;
        _logger.LogDebug("SFTP session established with {Host}:{Port}", _settings.Host, _settings.Port);
        return _client;
    }

    private void DisposeClientLocked()
    {
        if (_client is null) return;
        try
        {
            if (_client.IsConnected) _client.Disconnect();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SFTP disconnect failed during dispose (best-effort)");
        }
        try { _client.Dispose(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SFTP client dispose failed (best-effort)");
        }
        _client = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _semaphore.WaitAsync();
        try
        {
            DisposeClientLocked();
            _disposed = true;
        }
        finally
        {
            _semaphore.Release();
        }

        _semaphore.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SftpUploadProvider));
    }

    private void TryRemoveEmptyParents(SftpClient client, string fileRemotePath, string baseDir, CancellationToken ct)
    {
        var dir = GetParentPath(fileRemotePath);
        while (!string.IsNullOrEmpty(dir) && !string.Equals(dir, baseDir, StringComparison.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                client.DeleteDirectory(dir);
                _logger.LogDebug("SFTP removed empty directory '{Dir}'", dir);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SFTP keeping '{Dir}' (likely non-empty or no permission)", dir);
                return;
            }
            dir = GetParentPath(dir);
        }
    }

    private static string GetParentPath(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? string.Empty : path[..idx];
    }

    private SftpClient BuildClient()
    {
        var host = _settings.Host;
        var port = _settings.Port;
        var username = _settings.Username;

        SftpClient client;
        if (!string.IsNullOrWhiteSpace(_settings.PrivateKeyPath))
        {
            PrivateKeyFile keyFile = string.IsNullOrWhiteSpace(_settings.PrivateKeyPassphrase)
                ? new PrivateKeyFile(_settings.PrivateKeyPath)
                : new PrivateKeyFile(_settings.PrivateKeyPath, _settings.PrivateKeyPassphrase);

            _logger.LogDebug("SFTP using key-based auth for {Username}@{Host}:{Port}", username, host, port);
            client = new SftpClient(host, port, username, keyFile);
        }
        else
        {
            _logger.LogDebug("SFTP using password auth for {Username}@{Host}:{Port}", username, host, port);
            client = new SftpClient(host, port, username, _settings.Password);
        }

        client.KeepAliveInterval = TimeSpan.FromSeconds(15);
        client.HostKeyReceived += OnHostKeyReceived;
        return client;
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        var actual = ComputeFingerprint(e.HostKey);
        var expected = _settings.HostKeyFingerprint;

        if (string.IsNullOrWhiteSpace(expected))
        {
            if (Interlocked.Exchange(ref _warnedUntrusted, 1) == 0)
            {
                _logger.LogWarning(
                    "SFTP host key verification is disabled for {Host}:{Port}. " +
                    "Current host key fingerprint: {Fingerprint}. " +
                    "For production, set SftpSettings.HostKeyFingerprint to this value to prevent MITM.",
                    _settings.Host, _settings.Port, actual);
            }
            e.CanTrust = true;
            return;
        }

        if (!string.Equals(actual, expected.Trim(), StringComparison.Ordinal))
        {
            _logger.LogError(
                "SFTP host key mismatch for {Host}:{Port}. Expected: {Expected}, actual: {Actual}. " +
                "Connection refused — possible MITM or server key rotation.",
                _settings.Host, _settings.Port, expected, actual);
            e.CanTrust = false;
            return;
        }

        e.CanTrust = true;
    }

    private static void EnsureRemoteDirectory(SftpClient client, string remoteDir, CancellationToken ct)
    {
        var parts = remoteDir.TrimStart('/').Split('/');
        var current = string.Empty;

        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            current += "/" + part;
            if (!client.Exists(current))
                client.CreateDirectory(current);
        }
    }
}
