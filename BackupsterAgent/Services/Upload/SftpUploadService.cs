using BackupsterAgent.Settings;
using Renci.SshNet;

namespace BackupsterAgent.Services.Upload;

public sealed class SftpUploadService : IUploadService
{
    private readonly SftpSettings _settings;
    private readonly ILogger<SftpUploadService> _logger;

    public SftpUploadService(SftpSettings settings, ILogger<SftpUploadService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<string> UploadAsync(string filePath, string folder, IProgress<long>? progress, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);
        var remoteDir = $"{_settings.RemotePath.TrimEnd('/')}/{folder.TrimEnd('/')}";
        var remotePath = $"{remoteDir}/{fileName}";

        _logger.LogInformation(
            "SFTP uploading '{FilePath}' → {Host}:{RemotePath}",
            filePath, _settings.Host, remotePath);

        using var client = BuildClient();

        await Task.Run(() =>
        {
            client.Connect();

            EnsureRemoteDirectory(client, remoteDir);

            using var fileStream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 65536);

            Action<ulong>? callback = progress is null
                ? null
                : uploaded => progress.Report((long)uploaded);

            client.UploadFile(fileStream, remotePath, canOverride: true, uploadCallback: callback);

            client.Disconnect();
        }, ct);

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

    private SftpClient BuildClient()
    {
        var host = _settings.Host;
        var port = _settings.Port;
        var username = _settings.Username;

        if (!string.IsNullOrWhiteSpace(_settings.PrivateKeyPath))
        {
            PrivateKeyFile keyFile = string.IsNullOrWhiteSpace(_settings.PrivateKeyPassphrase)
                ? new PrivateKeyFile(_settings.PrivateKeyPath)
                : new PrivateKeyFile(_settings.PrivateKeyPath, _settings.PrivateKeyPassphrase);

            _logger.LogDebug("SFTP using key-based auth for {Username}@{Host}:{Port}", username, host, port);
            return new SftpClient(host, port, username, keyFile);
        }

        _logger.LogDebug("SFTP using password auth for {Username}@{Host}:{Port}", username, host, port);
        return new SftpClient(host, port, username, _settings.Password);
    }

    private static void EnsureRemoteDirectory(SftpClient client, string remoteDir)
    {
        var parts = remoteDir.TrimStart('/').Split('/');
        var current = string.Empty;

        foreach (var part in parts)
        {
            current += "/" + part;
            if (!client.Exists(current))
                client.CreateDirectory(current);
        }
    }
}
