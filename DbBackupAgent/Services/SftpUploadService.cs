using DbBackupAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace DbBackupAgent.Services;

public sealed class SftpUploadService : IUploadService
{
    private readonly SftpSettings _settings;
    private readonly ILogger<SftpUploadService> _logger;

    public SftpUploadService(IOptions<SftpSettings> settings, ILogger<SftpUploadService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<string> UploadAsync(string filePath, string folder, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);
        var remoteDir = $"{_settings.RemotePath.TrimEnd('/')}/{folder.TrimEnd('/')}";
        var remotePath = $"{remoteDir}/{fileName}";

        _logger.LogInformation(
            "SFTP uploading '{FilePath}' → {Host}:{RemotePath}",
            filePath, _settings.Host, remotePath);

        using var client = BuildClient();

        // SSH.NET is synchronous — run on thread pool to avoid blocking the async pipeline
        await Task.Run(() =>
        {
            client.Connect();

            EnsureRemoteDirectory(client, remoteDir);

            using var fileStream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 65536);

            client.UploadFile(fileStream, remotePath, canOverride: true);

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

    // -------------------------------------------------------------------------

    private SftpClient BuildClient()
    {
        var host = _settings.Host;
        var port = _settings.Port;
        var username = _settings.Username;

        if (!string.IsNullOrWhiteSpace(_settings.PrivateKeyPath))
        {
            // Key-based authentication
            PrivateKeyFile keyFile = string.IsNullOrWhiteSpace(_settings.PrivateKeyPassphrase)
                ? new PrivateKeyFile(_settings.PrivateKeyPath)
                : new PrivateKeyFile(_settings.PrivateKeyPath, _settings.PrivateKeyPassphrase);

            _logger.LogDebug("SFTP using key-based auth for {Username}@{Host}:{Port}", username, host, port);
            return new SftpClient(host, port, username, keyFile);
        }

        // Password authentication
        _logger.LogDebug("SFTP using password auth for {Username}@{Host}:{Port}", username, host, port);
        return new SftpClient(host, port, username, _settings.Password);
    }

    /// <summary>Creates the full remote directory path recursively if it doesn't exist.</summary>
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
