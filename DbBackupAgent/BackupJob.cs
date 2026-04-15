using DbBackupAgent.Models;
using DbBackupAgent.Providers;
using DbBackupAgent.Services;
using DbBackupAgent.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbBackupAgent;

public sealed class BackupJob
{
    private readonly IBackupProviderFactory _factory;
    private readonly EncryptionService _encryption;
    private readonly UploadServiceFactory _uploadFactory;
    private readonly FileSnapshotService _fileSnapshot;
    private readonly ReportService _report;
    private readonly AgentSettings _agentSettings;
    private readonly ILogger<BackupJob> _logger;

    public BackupJob(
        IBackupProviderFactory factory,
        EncryptionService encryption,
        UploadServiceFactory uploadFactory,
        FileSnapshotService fileSnapshot,
        ReportService report,
        IOptions<AgentSettings> agentSettings,
        ILogger<BackupJob> logger)
    {
        _factory = factory;
        _encryption = encryption;
        _uploadFactory = uploadFactory;
        _fileSnapshot = fileSnapshot;
        _report = report;
        _agentSettings = agentSettings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full backup pipeline:
    /// <list type="number">
    ///   <item>dump    → .sql.gz or .bak</item>
    ///   <item>encrypt → .enc</item>
    ///   <item>upload  → S3 / SFTP</item>
    ///   <item>cleanup → both local files removed (always, even on failure)</item>
    ///   <item>file snapshots → encrypt + upload each file from <c>FilePaths</c> (independent of DB result)</item>
    ///   <item>report  → POST /api/v1/agent/report</item>
    /// </list>
    /// </summary>
    public async Task<BackupResult> RunAsync(DatabaseConfig config, CancellationToken ct)
    {
        var provider = _factory.GetProvider(config.DatabaseType);

        _logger.LogInformation(
            "BackupJob starting. Provider: {ProviderType}, Database: '{Database}'",
            provider.GetType().Name, config.Database);

        string? dumpFile = null;
        string? encryptedFile = null;
        BackupResult result = new() { Success = false, ErrorMessage = "Unknown error" };

        try
        {
            // ── Step 1: dump ──────────────────────────────────────────────────
            _logger.LogInformation("Step 1/3: dump");
            var dumpResult = await provider.BackupAsync(config, ct);
            dumpFile = dumpResult.FilePath;

            // ── Step 2: encrypt ───────────────────────────────────────────────
            _logger.LogInformation("Step 2/3: encrypt");
            encryptedFile = await _encryption.EncryptAsync(dumpFile, ct);

            // ── Step 3: upload ────────────────────────────────────────────────
            _logger.LogInformation("Step 3/3: upload");
            var uploader = _uploadFactory.GetService();
            var storagePath = await uploader.UploadAsync(encryptedFile, config.Database, ct);

            result = new BackupResult
            {
                FilePath = dumpFile,
                SizeBytes = dumpResult.SizeBytes,
                DurationMs = dumpResult.DurationMs,
                Success = true,
                StoragePath = storagePath,
            };

            _logger.LogInformation(
                "BackupJob completed. File: '{FilePath}', Size: {SizeBytes} bytes, " +
                "Duration: {DurationMs} ms, StoragePath: '{StoragePath}'",
                result.FilePath, result.SizeBytes, result.DurationMs, result.StoragePath);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("BackupJob was cancelled");
            result = new BackupResult { Success = false, ErrorMessage = "Cancelled" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BackupJob failed");
            result = new BackupResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            // ── Step 4: cleanup — always runs ─────────────────────────────────
            _logger.LogInformation("Cleaning up local files");
            TryDelete(dumpFile);
            TryDelete(encryptedFile);
        }

        // ── Step 5: file snapshots — independent of DB result ────────────────
        if (config.FilePaths.Count > 0)
        {
            _logger.LogInformation(
                "Processing {Count} file path(s) for database '{Database}'",
                config.FilePaths.Count, config.Database);

            var uploader = _uploadFactory.GetService();
            foreach (var snapshotPath in config.FilePaths)
                await RunFileSnapshotAsync(snapshotPath, config.Database, uploader, ct);
        }

        // ── Step 6: report — always runs, success or failure ──────────────────
        await _report.ReportAsync(BuildReportDto(result, config), ct);

        return result;
    }

    private BackupReportDto BuildReportDto(BackupResult result, DatabaseConfig config) =>
        new()
        {
            DatabaseName = config.Database,
            Status = result.Success ? "success" : "failed",
            SizeBytes = result.SizeBytes,
            DurationMs = result.DurationMs,
            StoragePath = result.StoragePath ?? string.Empty,
            ErrorMessage = result.ErrorMessage,
            BackupAt = DateTime.UtcNow,
        };

    /// <summary>
    /// Encrypts and uploads every file found under <paramref name="snapshotPath"/>.
    /// Files are copied to a temp directory before encryption to avoid writing into
    /// system directories. Both the temp copy and its <c>.enc</c> are deleted afterwards.
    /// Errors on individual files are logged; processing continues for the rest.
    /// </summary>
    private async Task RunFileSnapshotAsync(
        string snapshotPath, string database, IUploadService uploader, CancellationToken ct)
    {
        var files = _fileSnapshot.GetFiles(snapshotPath);
        if (files.Count == 0) return;

        _logger.LogInformation(
            "Snapshot '{Path}': {Count} file(s) found", snapshotPath, files.Count);

        int succeeded = 0;
        int failed = 0;

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            string? tempCopy = null;
            string? encryptedFile = null;

            try
            {
                // Copy to temp dir to avoid writing .enc into source directories
                tempCopy = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                File.Copy(file, tempCopy, overwrite: true);

                encryptedFile = await _encryption.EncryptAsync(tempCopy, ct);

                // Storage key: {database}/files/{date}/{original-filename}.enc
                // achieved by passing "{database}/files" as the database parameter
                await uploader.UploadAsync(encryptedFile, $"{database}/files", ct);

                succeeded++;
                _logger.LogDebug("Snapshot file uploaded: '{File}'", file);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "Failed to snapshot file '{File}'", file);
            }
            finally
            {
                TryDelete(tempCopy);
                TryDelete(encryptedFile);
            }
        }

        _logger.LogInformation(
            "Snapshot '{Path}' complete. Succeeded: {Succeeded}, Failed: {Failed}",
            snapshotPath, succeeded, failed);
    }

    private void TryDelete(string? path)
    {
        if (path is null) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogDebug("Deleted local file '{Path}'", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete local file '{Path}'", path);
        }
    }
}
