using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Workers;

public sealed class RetentionWorker : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(15);

    private readonly RetentionSweepService _service;
    private readonly RetentionSettings _settings;
    private readonly IAgentActivityLock _lock;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(
        RetentionSweepService service,
        IOptions<RetentionSettings> settings,
        IAgentActivityLock activityLock,
        ILogger<RetentionWorker> logger)
    {
        _service = service;
        _settings = settings.Value;
        _lock = activityLock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("RetentionWorker: disabled by configuration.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _settings.IntervalHours));
        var batchSize = Math.Max(1, _settings.BatchSize);

        _logger.LogInformation(
            "RetentionWorker started. Interval: {IntervalH}h, batch: {Batch}, first run in {StartupMin} min.",
            interval.TotalHours, batchSize, StartupDelay.TotalMinutes);

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
                using var lease = await _lock.AcquireAsync("retention", stoppingToken);
                await _service.SweepAsync(batchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RetentionWorker: unexpected error in sweep loop.");
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

        _logger.LogInformation("RetentionWorker stopped.");
    }
}
