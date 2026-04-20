using System.Security.Cryptography;
using BackupsterAgent.Settings;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace BackupsterAgent.Services.Upload;

public sealed class SftpUploadService : IUploadService
{
    private readonly SftpSettings _settings;
    private readonly ILogger<SftpUploadService> _logger;
    private int _warnedUntrusted;

    public SftpUploadService(SftpSettings settings, ILogger<SftpUploadService> logger)
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

        using var client = BuildClient();

        try
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                client.Connect();

                using var reg = ct.Register(() =>
                {
                    try { client.Disconnect(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "SFTP disconnect on cancel failed (best-effort)"); }
                });

                EnsureRemoteDirectory(client, remoteDir, ct);

                using var fileStream = new FileStream(
                    filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 65536);

                Action<ulong>? callback = progress is null
                    ? null
                    : uploaded => progress.Report((long)uploaded);

                client.UploadFile(fileStream, remotePath, canOverride: true, uploadCallback: callback);

                ct.ThrowIfCancellationRequested();
                client.Disconnect();
            }, ct);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("SFTP upload cancelled by stoppingToken.", ex, ct);
        }

        var storagePath = $"sftp://{_settings.Host}{remotePath}";
        _logger.LogInformation("SFTP upload completed. StoragePath: '{StoragePath}'", storagePath);

        return storagePath;
    }

    public Task UploadBytesAsync(byte[] content, string objectKey, CancellationToken ct) =>
        throw new NotSupportedException("Byte-array upload is not supported for SFTP provider. File backup with deduplication requires S3.");

    public Task<bool> ExistsAsync(string objectKey, CancellationToken ct) =>
        throw new NotSupportedException("ExistsAsync is not supported for SFTP provider. File backup with deduplication requires S3.");

    public Task DownloadAsync(string objectKey, string localPath, IProgress<long>? progress, CancellationToken ct) =>
        throw new NotSupportedException("DownloadAsync is not supported for SFTP provider. Restore requires S3.");

    public Task<byte[]> DownloadBytesAsync(string objectKey, CancellationToken ct) =>
        throw new NotSupportedException("DownloadBytesAsync is not supported for SFTP provider. Restore requires S3.");

    public IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken ct) =>
        throw new NotSupportedException("ListAsync is not supported for SFTP provider. Chunk GC requires S3.");

    public async Task DeleteAsync(string objectKey, CancellationToken ct)
    {
        var remotePath = $"{_settings.RemotePath.TrimEnd('/')}/{objectKey.TrimStart('/')}";

        _logger.LogInformation(
            "SFTP deleting {Host}:{RemotePath}", _settings.Host, remotePath);

        using var client = BuildClient();

        try
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                client.Connect();

                using var reg = ct.Register(() =>
                {
                    try { client.Disconnect(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "SFTP disconnect on cancel failed (best-effort)"); }
                });

                try
                {
                    client.DeleteFile(remotePath);
                }
                catch (SftpPathNotFoundException)
                {
                    _logger.LogDebug(
                        "SFTP DeleteAsync: {RemotePath} not found — treating as already-deleted", remotePath);
                }

                ct.ThrowIfCancellationRequested();
                client.Disconnect();
            }, ct);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("SFTP delete cancelled by stoppingToken.", ex, ct);
        }

        _logger.LogInformation("SFTP delete completed. RemotePath: '{RemotePath}'", remotePath);
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
