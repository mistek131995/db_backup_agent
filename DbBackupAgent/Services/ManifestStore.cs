using System.Text.Json;
using DbBackupAgent.Models;
using Microsoft.Extensions.Logging;

namespace DbBackupAgent.Services;

public sealed class ManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly EncryptionService _encryption;
    private readonly IUploadServiceFactory _uploadFactory;
    private readonly ILogger<ManifestStore> _logger;

    public ManifestStore(
        EncryptionService encryption,
        IUploadServiceFactory uploadFactory,
        ILogger<ManifestStore> logger)
    {
        _encryption = encryption;
        _uploadFactory = uploadFactory;
        _logger = logger;
    }

    public async Task<string> SaveAsync(FileManifest manifest, string backupFolder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFolder);

        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var encrypted = _encryption.Encrypt(json);

        var objectKey = $"{backupFolder.TrimEnd('/')}/manifest.json.enc";
        await _uploadFactory.GetService().UploadBytesAsync(encrypted, objectKey, ct);

        _logger.LogInformation(
            "Manifest saved: {ObjectKey} ({PlaintextBytes} B plaintext, {EncryptedBytes} B encrypted)",
            objectKey, json.Length, encrypted.Length);

        return objectKey;
    }
}
