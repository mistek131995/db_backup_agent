using System.Text.Json;
using DbBackupAgent.Domain;
using DbBackupAgent.Services.Common;
using DbBackupAgent.Services.Upload;

namespace DbBackupAgent.Services.Backup;

public sealed class ManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly EncryptionService _encryption;
    private readonly ILogger<ManifestStore> _logger;

    public ManifestStore(
        EncryptionService encryption,
        ILogger<ManifestStore> logger)
    {
        _encryption = encryption;
        _logger = logger;
    }

    public async Task<string> SaveAsync(
        FileManifest manifest,
        string backupFolder,
        IUploadService uploader,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFolder);
        ArgumentNullException.ThrowIfNull(uploader);

        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var encrypted = _encryption.Encrypt(json);

        var objectKey = $"{backupFolder.TrimEnd('/')}/manifest.json.enc";
        await uploader.UploadBytesAsync(encrypted, objectKey, ct);

        _logger.LogInformation(
            "Manifest saved: {ObjectKey} ({PlaintextBytes} B plaintext, {EncryptedBytes} B encrypted)",
            objectKey, json.Length, encrypted.Length);

        return objectKey;
    }
}
