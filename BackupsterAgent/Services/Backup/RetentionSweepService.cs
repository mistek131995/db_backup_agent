using BackupsterAgent.Contracts;
using BackupsterAgent.Domain;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Dashboard;
using BackupsterAgent.Services.Delete;

namespace BackupsterAgent.Services.Backup;

public sealed class RetentionSweepService
{
    private readonly IRetentionClient _client;
    private readonly StorageResolver _storages;
    private readonly BackupDeleteService _deleter;
    private readonly ILogger<RetentionSweepService> _logger;

    public RetentionSweepService(
        IRetentionClient client,
        StorageResolver storages,
        BackupDeleteService deleter,
        ILogger<RetentionSweepService> logger)
    {
        _client = client;
        _storages = storages;
        _deleter = deleter;
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
        int deleted = 0, failed = 0;

        foreach (var record in batch)
        {
            if (ct.IsCancellationRequested) break;

            if (string.IsNullOrWhiteSpace(record.StorageName) ||
                !_storages.TryResolve(record.StorageName, out _))
            {
                unreachable.Add(record.Id);
                continue;
            }

            var payload = new DeleteTaskPayload
            {
                StorageName = record.StorageName,
                DumpObjectKey = record.DumpObjectKey,
                ManifestKey = record.ManifestKey,
            };

            BackupDeleteResult result;
            try
            {
                result = await _deleter.RunAsync(record.Id, payload, reporter: null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }

            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "Retention: failed to clean up record {Id} on storage '{Storage}': {Error}. Will retry next tick.",
                    record.Id, record.StorageName, result.ErrorMessage);
                failed++;
                continue;
            }

            try
            {
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
                    "Retention: storage cleaned but dashboard DELETE failed for record {Id}. Will retry next tick.",
                    record.Id);
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
            "Retention sweep finished: batch={Batch}, deleted={Deleted}, unreachable={Unreachable}, failed={Failed}.",
            batch.Count, deleted, unreachable.Count, failed);
    }
}
