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
    private readonly IUploadServiceFactory _uploadFactory;
    private readonly FileBackupService _fileBackup;
    private readonly ManifestStore _manifestStore;
    private readonly ReportService _report;
    private readonly UploadSettings _uploadSettings;
    private readonly AgentSettings _agentSettings;
    private readonly ILogger<BackupJob> _logger;

    public BackupJob(
        IBackupProviderFactory factory,
        EncryptionService encryption,
        IUploadServiceFactory uploadFactory,
        FileBackupService fileBackup,
        ManifestStore manifestStore,
        ReportService report,
        IOptions<UploadSettings> uploadSettings,
        IOptions<AgentSettings> agentSettings,
        ILogger<BackupJob> logger)
    {
        _factory = factory;
        _encryption = encryption;
        _uploadFactory = uploadFactory;
        _fileBackup = fileBackup;
        _manifestStore = manifestStore;
        _report = report;
        _uploadSettings = uploadSettings.Value;
        _agentSettings = agentSettings.Value;
        _logger = logger;
    }

    public async Task<BackupResult> RunAsync(DatabaseConfig config, CancellationToken ct)
    {
        var provider = _factory.GetProvider(config.DatabaseType);
        var backupFolder = $"{config.Database}_{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}";

        _logger.LogInformation(
            "BackupJob starting. Provider: {ProviderType}, Database: '{Database}', Folder: '{Folder}'",
            provider.GetType().Name, config.Database, backupFolder);

        string? dumpFile = null;
        string? encryptedFile = null;
        string? dumpObjectKey = null;
        BackupResult result = new() { Success = false, ErrorMessage = "Unknown error" };

        try
        {
            _logger.LogInformation("Step 1/3: dump");
            var dumpResult = await provider.BackupAsync(config, ct);
            dumpFile = dumpResult.FilePath;

            _logger.LogInformation("Step 2/3: encrypt");
            encryptedFile = await _encryption.EncryptAsync(dumpFile, ct);

            _logger.LogInformation("Step 3/3: upload");
            var uploader = _uploadFactory.GetService();
            var storagePath = await uploader.UploadAsync(encryptedFile, backupFolder, ct);
            dumpObjectKey = $"{backupFolder}/{Path.GetFileName(encryptedFile)}";

            result = new BackupResult
            {
                FilePath = dumpFile,
                SizeBytes = dumpResult.SizeBytes,
                DurationMs = dumpResult.DurationMs,
                Success = true,
                StoragePath = storagePath,
            };

            _logger.LogInformation(
                "Dump uploaded. File: '{FilePath}', Size: {SizeBytes} bytes, " +
                "Duration: {DurationMs} ms, StoragePath: '{StoragePath}'",
                result.FilePath, result.SizeBytes, result.DurationMs, result.StoragePath);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("BackupJob was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BackupJob failed");
            result = new BackupResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            TryDelete(dumpFile);
            TryDelete(encryptedFile);
        }

        var (fileMetrics, fileError) = await CaptureFilesSafelyAsync(config, backupFolder, dumpObjectKey, ct);

        await _report.ReportAsync(BuildReportDto(result, config, fileMetrics, fileError), ct);

        return result;
    }

    internal async Task<(FileBackupMetrics? Metrics, string? Error)> CaptureFilesSafelyAsync(
        DatabaseConfig config, string backupFolder, string? dumpObjectKey, CancellationToken ct)
    {
        if (config.FilePaths.Count == 0)
            return (null, null);

        if (_uploadSettings.Provider.Equals("Sftp", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "File backup is not supported with SFTP provider. " +
                "Skipping {Count} file path(s) for database '{Database}'",
                config.FilePaths.Count, config.Database);
            return (null, "Бэкап файлов не поддерживается с SFTP-провайдером. Файлы не загружены.");
        }

        try
        {
            _logger.LogInformation(
                "Capturing file backup for database '{Database}' ({Count} path(s))",
                config.Database, config.FilePaths.Count);

            var capture = await _fileBackup.CaptureAsync(config.FilePaths, ct);
            var manifest = capture.Manifest with
            {
                Database = config.Database,
                DumpObjectKey = dumpObjectKey ?? string.Empty,
            };
            var manifestKey = await _manifestStore.SaveAsync(manifest, backupFolder, ct);

            var metrics = new FileBackupMetrics
            {
                ManifestKey = manifestKey,
                FilesCount = manifest.Files.Count,
                FilesTotalBytes = manifest.Files.Sum(f => f.Size),
                NewChunksCount = capture.NewChunksCount,
            };
            return (metrics, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File backup failed for database '{Database}'", config.Database);
            return (null, "Не удалось загрузить файлы в хранилище. Подробности — в логах агента.");
        }
    }

    private static BackupReportDto BuildReportDto(
        BackupResult result, DatabaseConfig config, FileBackupMetrics? fileMetrics, string? fileBackupError) =>
        new()
        {
            DatabaseName = config.Database,
            Status = result.Success ? "success" : "failed",
            SizeBytes = result.SizeBytes,
            DurationMs = result.DurationMs,
            StoragePath = result.StoragePath ?? string.Empty,
            ErrorMessage = result.ErrorMessage,
            BackupAt = DateTime.UtcNow,
            ManifestKey = fileMetrics?.ManifestKey,
            FilesCount = fileMetrics?.FilesCount,
            FilesTotalBytes = fileMetrics?.FilesTotalBytes,
            NewChunksCount = fileMetrics?.NewChunksCount,
            FileBackupError = fileBackupError,
        };

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

    internal sealed class FileBackupMetrics
    {
        public required string ManifestKey { get; init; }
        public required int FilesCount { get; init; }
        public required long FilesTotalBytes { get; init; }
        public required int NewChunksCount { get; init; }
    }
}
