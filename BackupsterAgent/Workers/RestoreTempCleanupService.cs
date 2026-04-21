using BackupsterAgent.Services.Restore;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Workers;

public sealed class RestoreTempCleanupService : IHostedService
{
    private readonly RestoreSettings _settings;
    private readonly ILogger<RestoreTempCleanupService> _logger;

    public RestoreTempCleanupService(
        IOptions<RestoreSettings> settings,
        ILogger<RestoreTempCleanupService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var tempRoot = DatabaseRestoreService.BuildTempRoot(_settings.TempPath);
        try
        {
            if (!Directory.Exists(tempRoot))
            {
                _logger.LogDebug("Restore temp root '{TempRoot}' does not exist, nothing to clean.", tempRoot);
                return Task.CompletedTask;
            }

            var entries = Directory.EnumerateFileSystemEntries(tempRoot).ToList();
            if (entries.Count == 0)
            {
                _logger.LogDebug("Restore temp root '{TempRoot}' is already clean.", tempRoot);
                return Task.CompletedTask;
            }

            _logger.LogInformation(
                "Cleaning {Count} orphan entries from restore temp root '{TempRoot}'",
                entries.Count, tempRoot);

            foreach (var entry in entries)
            {
                try
                {
                    if (Directory.Exists(entry))
                        Directory.Delete(entry, recursive: true);
                    else
                        File.Delete(entry);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete orphan temp entry '{Entry}'", entry);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean restore temp root '{TempRoot}'", tempRoot);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
