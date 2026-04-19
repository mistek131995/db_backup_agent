using System.Text;
using System.Text.Json;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Upload;

namespace BackupsterAgent.Services.Backup;

public sealed class ChunkGcService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const string ManifestSuffix = "/manifest.json.enc";
    private const string ChunksPrefix = "chunks/";

    private readonly StorageResolver _storages;
    private readonly IUploadServiceFactory _factory;
    private readonly EncryptionService _encryption;
    private readonly ILogger<ChunkGcService> _logger;

    public ChunkGcService(
        StorageResolver storages,
        IUploadServiceFactory factory,
        EncryptionService encryption,
        ILogger<ChunkGcService> logger)
    {
        _storages = storages;
        _factory = factory;
        _encryption = encryption;
        _logger = logger;
    }

    public async Task SweepAllAsync(TimeSpan graceWindow, CancellationToken ct)
    {
        if (!_encryption.IsConfigured)
        {
            _logger.LogWarning("ChunkGc: encryption key is not configured, sweep skipped.");
            return;
        }

        if (_storages.Names.Count == 0)
        {
            _logger.LogDebug("ChunkGc: no storages configured, nothing to sweep.");
            return;
        }

        foreach (var name in _storages.Names)
        {
            if (ct.IsCancellationRequested) break;

            var storage = _storages.Resolve(name);
            if (storage.Provider != UploadProvider.S3)
            {
                _logger.LogDebug(
                    "ChunkGc: skipping storage '{Storage}' — provider {Provider} has no chunk pool.",
                    name, storage.Provider);
                continue;
            }

            try
            {
                await SweepStorageAsync(name, graceWindow, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChunkGc: sweep failed for storage '{Storage}'.", name);
            }
        }
    }

    private async Task SweepStorageAsync(string storageName, TimeSpan graceWindow, CancellationToken ct)
    {
        var uploader = _factory.GetService(storageName);

        _logger.LogInformation(
            "ChunkGc: starting sweep for storage '{Storage}' (grace: {GraceH}h).",
            storageName, graceWindow.TotalHours);

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        int manifestCount = 0;

        await foreach (var obj in uploader.ListAsync(string.Empty, ct))
        {
            if (!obj.Key.EndsWith(ManifestSuffix, StringComparison.Ordinal)) continue;

            byte[] encrypted;
            try
            {
                encrypted = await uploader.DownloadBytesAsync(obj.Key, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ChunkGc: failed to download manifest '{Key}' in storage '{Storage}'. Aborting sweep.",
                    obj.Key, storageName);
                return;
            }

            FileManifest? manifest;
            try
            {
                var aad = Encoding.UTF8.GetBytes(obj.Key);
                var json = _encryption.Decrypt(encrypted, aad);
                manifest = JsonSerializer.Deserialize<FileManifest>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ChunkGc: failed to decrypt/parse manifest '{Key}' in storage '{Storage}'. Aborting sweep.",
                    obj.Key, storageName);
                return;
            }

            if (manifest is null) continue;
            manifestCount++;

            foreach (var entry in manifest.Files)
                foreach (var chunk in entry.Chunks)
                    referenced.Add(chunk);
        }

        var cutoff = DateTime.UtcNow - graceWindow;
        int totalChunks = 0;
        int deleted = 0;
        int skippedGrace = 0;
        long freedBytes = 0;

        await foreach (var obj in uploader.ListAsync(ChunksPrefix, ct))
        {
            totalChunks++;

            if (!obj.Key.StartsWith(ChunksPrefix, StringComparison.Ordinal)) continue;
            var sha = obj.Key[ChunksPrefix.Length..];
            if (sha.Length == 0) continue;
            if (referenced.Contains(sha)) continue;

            if (obj.LastModifiedUtc > cutoff)
            {
                skippedGrace++;
                continue;
            }

            try
            {
                await uploader.DeleteAsync(obj.Key, ct);
                deleted++;
                freedBytes += obj.Size;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ChunkGc: failed to delete chunk '{Key}' in storage '{Storage}'.",
                    obj.Key, storageName);
            }
        }

        _logger.LogInformation(
            "ChunkGc: storage '{Storage}' — manifests: {Manifests}, referenced: {Refs}, total chunks: {Total}, deleted: {Deleted} ({FreedMb:F1} MB), skipped (grace): {Grace}.",
            storageName, manifestCount, referenced.Count, totalChunks, deleted, freedBytes / 1024.0 / 1024.0, skippedGrace);
    }
}
