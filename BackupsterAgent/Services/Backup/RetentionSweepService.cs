using BackupsterAgent.Contracts;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Dashboard;
using BackupsterAgent.Services.Upload;

namespace BackupsterAgent.Services.Backup;

public sealed class RetentionSweepService
{
    private readonly IRetentionClient _client;
    private readonly StorageResolver _storages;
    private readonly IUploadServiceFactory _uploadFactory;
    private readonly ILogger<RetentionSweepService> _logger;

    public RetentionSweepService(
        IRetentionClient client,
        StorageResolver storages,
        IUploadServiceFactory uploadFactory,
        ILogger<RetentionSweepService> logger)
    {
        _client = client;
        _storages = storages;
        _uploadFactory = uploadFactory;
        _logger = logger;
    }

    public async Task SweepAsync(int batchSize, CancellationToken ct)
    {
        IReadOnlyList<ExpiredBackupRecordDto> batch;
        try
        {
            batch = await _client.GetExpiredAsync(batchSize, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention sweep: failed to fetch expired batch.");
            return;
        }

        if (batch.Count == 0)
        {
            _logger.LogDebug("Retention sweep: no expired records.");
            return;
        }

        var unreachable = new List<Guid>();
        int deleted = 0, skippedNonS3 = 0, failed = 0;

        foreach (var record in batch)
        {
            if (ct.IsCancellationRequested) break;

            if (string.IsNullOrWhiteSpace(record.StorageName) ||
                !_storages.TryResolve(record.StorageName, out var storage))
            {
                unreachable.Add(record.Id);
                continue;
            }

            if (storage.Provider != UploadProvider.S3)
            {
                _logger.LogDebug(
                    "Retention: record {Id} on storage '{Storage}' (provider {Provider}) — only S3 retention is supported, skipping.",
                    record.Id, record.StorageName, storage.Provider);
                skippedNonS3++;
                continue;
            }

            try
            {
                var uploader = _uploadFactory.GetService(record.StorageName);

                if (!string.IsNullOrEmpty(record.DumpObjectKey))
                    await uploader.DeleteAsync(record.DumpObjectKey, ct);

                if (!string.IsNullOrEmpty(record.ManifestKey))
                    await uploader.DeleteAsync(record.ManifestKey, ct);

                await _client.DeleteAsync(record.Id, ct);
                deleted++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Retention: failed to clean up record {Id} on storage '{Storage}'. Will retry next tick.",
                    record.Id, record.StorageName);
                failed++;
            }
        }

        if (unreachable.Count > 0)
        {
            try
            {
                await _client.MarkStorageUnreachableAsync(unreachable, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Retention: failed to mark {Count} record(s) as storage-unreachable. Will retry next tick.",
                    unreachable.Count);
            }
        }

        _logger.LogInformation(
            "Retention sweep finished: batch={Batch}, deleted={Deleted}, unreachable={Unreachable}, skippedNonS3={SkippedNonS3}, failed={Failed}.",
            batch.Count, deleted, unreachable.Count, skippedNonS3, failed);
    }
}
