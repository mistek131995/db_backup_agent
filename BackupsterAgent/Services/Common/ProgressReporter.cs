namespace BackupsterAgent.Services.Common;

public sealed class ProgressReporter<TStage> : IProgressReporter<TStage>
    where TStage : struct, Enum
{
    private static readonly TimeSpan MinSendInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private readonly Func<ProgressSnapshot<TStage>, CancellationToken, Task> _sink;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _heartbeat;
    private readonly object _gate = new();

    private ProgressSnapshot<TStage>? _latest;
    private TStage _lastSentStage;
    private bool _hasSent;
    private DateTime _lastSentAt = DateTime.MinValue;
    private bool _disposed;

    public ProgressReporter(
        Func<ProgressSnapshot<TStage>, CancellationToken, Task> sink,
        ILogger logger)
    {
        _sink = sink;
        _logger = logger;
        _heartbeat = new Timer(_ => OnHeartbeat(), null, HeartbeatInterval, HeartbeatInterval);
    }

    public void Report(
        TStage stage,
        long? processed = null,
        long? total = null,
        string? unit = null,
        string? currentItem = null)
    {
        var snap = new ProgressSnapshot<TStage>(stage, processed, total, unit, currentItem);

        bool shouldSend;
        lock (_gate)
        {
            if (_disposed) return;
            _latest = snap;
            var stageChanged = !_hasSent || !_lastSentStage.Equals(stage);
            var elapsed = DateTime.UtcNow - _lastSentAt;
            shouldSend = stageChanged || elapsed >= MinSendInterval;
        }

        if (shouldSend)
            _ = Task.Run(TrySendAsync);
    }

    private void OnHeartbeat()
    {
        lock (_gate)
        {
            if (_disposed || _latest is null) return;
        }
        _ = Task.Run(TrySendAsync);
    }

    private async Task TrySendAsync()
    {
        if (!await _sendLock.WaitAsync(0)) return;
        try
        {
            ProgressSnapshot<TStage>? snap;
            CancellationToken ct;
            lock (_gate)
            {
                if (_disposed) return;
                snap = _latest;
                ct = _cts.Token;
            }
            if (snap is null) return;

            try
            {
                await _sink(snap, ct);
                lock (_gate)
                {
                    _lastSentStage = snap.Stage;
                    _hasSent = true;
                    _lastSentAt = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ProgressReporter: send failed (swallowed)");
            }
        }
        finally
        {
            try { _sendLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        await _heartbeat.DisposeAsync();

        try { _cts.Cancel(); } catch (ObjectDisposedException) { }

        try { await _sendLock.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }

        _cts.Dispose();
        _sendLock.Dispose();
    }
}
