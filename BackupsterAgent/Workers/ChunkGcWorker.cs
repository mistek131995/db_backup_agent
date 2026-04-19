using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Workers;

public sealed class ChunkGcWorker : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);

    private readonly ChunkGcService _service;
    private readonly GcSettings _settings;
    private readonly IAgentActivityLock _lock;
    private readonly ILogger<ChunkGcWorker> _logger;

    public ChunkGcWorker(
        ChunkGcService service,
        IOptions<GcSettings> settings,
        IAgentActivityLock activityLock,
        ILogger<ChunkGcWorker> logger)
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
                await _service.SweepAllAsync(grace, stoppingToken);
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
}
