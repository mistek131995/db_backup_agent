using BackupsterAgent.Domain;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Dashboard;
using BackupsterAgent.Settings;
using BackupsterAgent.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Tests.Workers;

[TestFixture]
public sealed class OutboxReplayWorkerTests
{
    private string _root = null!;
    private OutboxStore _store = null!;
    private FakeBackupRecordClient _client = null!;
    private OutboxReplayWorker _worker = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"outbox-worker-{Guid.NewGuid():N}");
        _store = new OutboxStore(_root, NullLogger<OutboxStore>.Instance);
        _client = new FakeBackupRecordClient();
        _worker = new OutboxReplayWorker(
            _store, _client,
            Options.Create(new OutboxSettings()),
            NullLogger<OutboxReplayWorker>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _worker.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Test]
    public async Task TickAsync_EmptyOutbox_NoClientCalls()
    {
        await _worker.TickAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_client.OpenCalls, Is.Zero);
            Assert.That(_client.FinalizeCalls, Is.Zero);
        });
    }

    [Test]
    public async Task TickAsync_EntryWithoutServerId_OpensThenFinalizes_RemovesEntry()
    {
        var serverId = Guid.NewGuid();
        _client.NextOpen = new OpenRecordResult(DashboardAvailability.Ok, serverId);
        _client.NextFinalize = new FinalizeRecordResult(DashboardAvailability.Ok);
        await _store.EnqueueAsync(MakeEntry("t1", serverRecordId: null), CancellationToken.None);

        await _worker.TickAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_client.OpenCalls, Is.EqualTo(1));
            Assert.That(_client.FinalizeCalls, Is.EqualTo(1));
        });

        var entries = await _store.ListAsync(CancellationToken.None);
        Assert.That(entries, Is.Empty);
    }

    [Test]
    public async Task TickAsync_EntryWithServerId_SkipsOpen_FinalizesOnly_RemovesEntry()
    {
        var serverId = Guid.NewGuid();
        _client.NextFinalize = new FinalizeRecordResult(DashboardAvailability.Ok);
        await _store.EnqueueAsync(MakeEntry("t1", serverRecordId: serverId), CancellationToken.None);

        await _worker.TickAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_client.OpenCalls, Is.Zero, "open must be skipped when serverRecordId is set");
            Assert.That(_client.FinalizeCalls, Is.EqualTo(1));
        });

        var entries = await _store.ListAsync(CancellationToken.None);
        Assert.That(entries, Is.Empty);
    }

    [Test]
    public async Task TickAsync_OpenOfflineRetryable_IncrementsAttempt_KeepsEntry()
    {
        _client.NextOpen = new OpenRecordResult(DashboardAvailability.OfflineRetryable);
        await _store.EnqueueAsync(MakeEntry("t1", serverRecordId: null), CancellationToken.None);

        await _worker.TickAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_client.OpenCalls, Is.EqualTo(1));
            Assert.That(_client.FinalizeCalls, Is.Zero);
        });

        var entries = await _store.ListAsync(CancellationToken.None);
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].AttemptCount, Is.EqualTo(1));
            Assert.That(entries[0].ServerRecordId, Is.Null);
        });
    }

    [Test]
    public async Task TickAsync_FinalizeOfflineRetryable_PersistsServerId_KeepsEntry()
    {
        var serverId = Guid.NewGuid();
        _client.NextOpen = new OpenRecordResult(DashboardAvailability.Ok, serverId);
        _client.NextFinalize = new FinalizeRecordResult(DashboardAvailability.OfflineRetryable);
        await _store.EnqueueAsync(MakeEntry("t1", serverRecordId: null), CancellationToken.None);

        await _worker.TickAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_client.OpenCalls, Is.EqualTo(1));
            Assert.That(_client.FinalizeCalls, Is.EqualTo(1));
        });

        var entries = await _store.ListAsync(CancellationToken.None);
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].ServerRecordId, Is.EqualTo(serverId));
            Assert.That(entries[0].AttemptCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task TickAsync_OpenPermanentSkip_MovesToDeadLetter()
    {
        _client.NextOpen = new OpenRecordResult(DashboardAvailability.PermanentSkip);
        await _store.EnqueueAsync(MakeEntry("t1", serverRecordId: null), CancellationToken.None);

        await _worker.TickAsync(CancellationToken.None);

        var entries = await _store.ListAsync(CancellationToken.None);
        Assert.That(entries, Is.Empty);
        Assert.That(File.Exists(Path.Combine(_root, "dead", "t1.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(_root, "dead", "t1.reason.txt")), Is.True);
    }

    [Test]
    public async Task TickAsync_FinalizePermanentSkip_MovesToDeadLetter()
    {
        var serverId = Guid.NewGuid();
        _client.NextFinalize = new FinalizeRecordResult(DashboardAvailability.PermanentSkip);
        await _store.EnqueueAsync(MakeEntry("t1", serverRecordId: serverId), CancellationToken.None);

        await _worker.TickAsync(CancellationToken.None);

        var entries = await _store.ListAsync(CancellationToken.None);
        Assert.That(entries, Is.Empty);
        Assert.That(File.Exists(Path.Combine(_root, "dead", "t1.json")), Is.True);
    }

    [Test]
    public async Task TickAsync_ExceededMaxAttempts_MovesToDeadWithoutCallingClient()
    {
        await _store.EnqueueAsync(MakeEntry("t1", serverRecordId: null) with { AttemptCount = 200 }, CancellationToken.None);

        await _worker.TickAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_client.OpenCalls, Is.Zero);
            Assert.That(_client.FinalizeCalls, Is.Zero);
        });
        Assert.That(File.Exists(Path.Combine(_root, "dead", "t1.json")), Is.True);
    }

    [Test]
    public async Task TickAsync_MultipleEntries_OfflineStopsFurtherAttempts()
    {
        _client.NextOpen = new OpenRecordResult(DashboardAvailability.OfflineRetryable);

        await _store.EnqueueAsync(MakeEntry("t1", serverRecordId: null, queuedAt: DateTime.UtcNow.AddMinutes(-10)), CancellationToken.None);
        await _store.EnqueueAsync(MakeEntry("t2", serverRecordId: null, queuedAt: DateTime.UtcNow.AddMinutes(-5)), CancellationToken.None);

        await _worker.TickAsync(CancellationToken.None);

        Assert.That(_client.OpenCalls, Is.EqualTo(1), "after first offline failure, second entry must not be attempted");
        var entries = await _store.ListAsync(CancellationToken.None);
        Assert.That(entries, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task TickAsync_OpenReturnsOkWithEmptyId_MovesToDeadLetter()
    {
        _client.NextOpen = new OpenRecordResult(DashboardAvailability.Ok, Guid.Empty);
        await _store.EnqueueAsync(MakeEntry("t1", serverRecordId: null), CancellationToken.None);

        await _worker.TickAsync(CancellationToken.None);

        var entries = await _store.ListAsync(CancellationToken.None);
        Assert.That(entries, Is.Empty);
        Assert.That(File.Exists(Path.Combine(_root, "dead", "t1.json")), Is.True);
    }

    [Test]
    public async Task TickAsync_EntryPassesCorrectOpenDtoFields()
    {
        var serverId = Guid.NewGuid();
        _client.NextOpen = new OpenRecordResult(DashboardAvailability.Ok, serverId);
        _client.NextFinalize = new FinalizeRecordResult(DashboardAvailability.Ok);

        var entry = MakeEntry("t1", serverRecordId: null) with
        {
            DatabaseName = "payments",
            ConnectionName = "pg-main",
            StorageName = "s3-prod",
            StartedAt = new DateTime(2026, 4, 20, 2, 0, 0, DateTimeKind.Utc),
        };
        await _store.EnqueueAsync(entry, CancellationToken.None);

        await _worker.TickAsync(CancellationToken.None);

        Assert.That(_client.LastOpen, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(_client.LastOpen!.DatabaseName, Is.EqualTo("payments"));
            Assert.That(_client.LastOpen.ConnectionName, Is.EqualTo("pg-main"));
            Assert.That(_client.LastOpen.StorageName, Is.EqualTo("s3-prod"));
            Assert.That(_client.LastOpen.StartedAt, Is.EqualTo(entry.StartedAt));
        });
    }

    private static OutboxEntry MakeEntry(string taskId, Guid? serverRecordId, DateTime? queuedAt = null) => new()
    {
        ClientTaskId = taskId,
        DatabaseName = "db1",
        ConnectionName = "conn1",
        StorageName = "s3-main",
        StartedAt = new DateTime(2026, 4, 20, 2, 0, 0, DateTimeKind.Utc),
        BackupAt = new DateTime(2026, 4, 20, 2, 15, 0, DateTimeKind.Utc),
        Status = "success",
        SizeBytes = 1_000_000,
        DurationMs = 15 * 60 * 1000,
        DumpObjectKey = $"db1/2026-04-20_02-00-00/dump.sql.gz.enc",
        QueuedAt = queuedAt ?? DateTime.UtcNow,
        ServerRecordId = serverRecordId,
    };
}
