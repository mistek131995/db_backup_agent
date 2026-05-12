using BackupsterAgent.Configuration;
using BackupsterAgent.Enums;
using BackupsterAgent.Providers.Upload;
using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Common.Resolvers;
using BackupsterAgent.Services.Common.Security;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Workers;

public sealed class ChunkGcWorker : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);
    private const string ChunksPrefix = "chunks/";

    private readonly StorageResolver _storages;
    private readonly IUploadProviderFactory _uploadFactory;
    private readonly EncryptionService _encryption;
    private readonly ManifestStore _manifestStore;
    private readonly GcSettings _settings;
    private readonly IAgentActivityLock _lock;
    private readonly ILogger<ChunkGcWorker> _logger;

    public ChunkGcWorker(
        StorageResolver storages,
        IUploadProviderFactory uploadFactory,
        EncryptionService encryption,
        ManifestStore manifestStore,
        IOptions<GcSettings> settings,
        IAgentActivityLock activityLock,
        ILogger<ChunkGcWorker> logger)
    {
        _storages = storages;
        _uploadFactory = uploadFactory;
        _encryption = encryption;
        _manifestStore = manifestStore;
        _settings = settings.Value;
        _lock = activityLock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("ChunkGcWorker: disabled by configuration.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _settings.IntervalHours));
        var grace = TimeSpan.FromHours(Math.Max(0, _settings.GraceHours));

        _logger.LogInformation(
            "ChunkGcWorker started. Interval: {IntervalH}h, grace: {GraceH}h, first run in {StartupMin} min.",
            interval.TotalHours, grace.TotalHours, StartupDelay.TotalMinutes);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var lease = await _lock.AcquireAsync("chunk-gc", stoppingToken);
                await SweepAllAsync(grace, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChunkGcWorker: unexpected error in sweep loop.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("ChunkGcWorker stopped.");
    }

    private async Task SweepAllAsync(TimeSpan graceWindow, CancellationToken ct)
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
            if (storage.Provider is not (UploadProvider.S3 or UploadProvider.AzureBlob or UploadProvider.LocalFs or UploadProvider.Sftp or UploadProvider.WebDav))
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
        var uploader = _uploadFactory.GetProvider(storageName);

        _logger.LogInformation(
            "ChunkGc: starting sweep for storage '{Storage}' (grace: {GraceH}h).",
            storageName, graceWindow.TotalHours);

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        int manifestCount = 0;

        await foreach (var obj in uploader.ListAsync(string.Empty, ct))
        {
            if (!IsManifestKey(obj.Key)) continue;

            try
            {
                await using var reader = await _manifestStore.OpenReaderAsync(obj.Key, uploader, ct);

                await foreach (var entry in reader.ReadFilesAsync(ct))
                {
                    foreach (var chunk in entry.Chunks)
                        referenced.Add(chunk);
                }

                manifestCount++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ChunkGc: failed to read manifest '{Key}' in storage '{Storage}'. Aborting sweep.",
                    obj.Key, storageName);
                return;
            }
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

    private static bool IsManifestKey(string key) =>
        key.EndsWith(ManifestStore.NewSuffix, StringComparison.Ordinal) ||
        key.EndsWith(ManifestStore.LegacySuffix, StringComparison.Ordinal);
}
