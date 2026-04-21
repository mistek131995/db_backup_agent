using BackupsterAgent.Domain;
using BackupsterAgent.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupsterAgent.Tests.Services;

[TestFixture]
public sealed class OutboxStoreTests
{
    private string _root = null!;
    private OutboxStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"outbox-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new OutboxStore(_root, NullLogger<OutboxStore>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Test]
    public async Task EnqueueAsync_ThenListAsync_RoundTripsEntry()
    {
        var entry = MakeEntry("task-1", "payments", "pg-main");

        await _store.EnqueueAsync(entry, CancellationToken.None);
        var listed = await _store.ListAsync(CancellationToken.None);

        Assert.That(listed, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(listed[0].ClientTaskId, Is.EqualTo("task-1"));
            Assert.That(listed[0].DatabaseName, Is.EqualTo("payments"));
            Assert.That(listed[0].ConnectionName, Is.EqualTo("pg-main"));
            Assert.That(listed[0].Status, Is.EqualTo("success"));
        });
    }

    [Test]
    public async Task EnqueueAsync_SameClientTaskId_OverwritesExisting()
    {
        await _store.EnqueueAsync(MakeEntry("task-1", "db1", "c1") with { AttemptCount = 0 }, CancellationToken.None);
        await _store.EnqueueAsync(MakeEntry("task-1", "db1", "c1") with { AttemptCount = 5 }, CancellationToken.None);

        var listed = await _store.ListAsync(CancellationToken.None);

        Assert.That(listed, Has.Count.EqualTo(1));
        Assert.That(listed[0].AttemptCount, Is.EqualTo(5));
    }

    [Test]
    public void EnqueueAsync_EmptyClientTaskId_Throws()
    {
        var entry = MakeEntry(string.Empty, "db1", "c1");

        Assert.ThrowsAsync<ArgumentException>(() =>
            _store.EnqueueAsync(entry, CancellationToken.None));
    }

    [Test]
    public async Task ListAsync_EmptyDirectory_ReturnsEmpty()
    {
        var listed = await _store.ListAsync(CancellationToken.None);

        Assert.That(listed, Is.Empty);
    }

    [Test]
    public async Task ListAsync_NonexistentDirectory_ReturnsEmpty()
    {
        Directory.Delete(_root, recursive: true);

        var listed = await _store.ListAsync(CancellationToken.None);

        Assert.That(listed, Is.Empty);
    }

    [Test]
    public async Task ListAsync_MultipleEntries_SortedByQueuedAt()
    {
        var baseTime = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);
        await _store.EnqueueAsync(MakeEntry("task-c", "db1", "c1") with { QueuedAt = baseTime.AddMinutes(20) }, CancellationToken.None);
        await _store.EnqueueAsync(MakeEntry("task-a", "db1", "c1") with { QueuedAt = baseTime.AddMinutes(5) }, CancellationToken.None);
        await _store.EnqueueAsync(MakeEntry("task-b", "db1", "c1") with { QueuedAt = baseTime.AddMinutes(10) }, CancellationToken.None);

        var listed = await _store.ListAsync(CancellationToken.None);

        Assert.That(listed.Select(e => e.ClientTaskId).ToList(),
            Is.EqualTo(new[] { "task-a", "task-b", "task-c" }));
    }

    [Test]
    public async Task ListAsync_CorruptJsonFile_SkipsAndContinues()
    {
        await _store.EnqueueAsync(MakeEntry("task-good", "db1", "c1"), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_root, "garbage.json"), "{not valid json");

        var listed = await _store.ListAsync(CancellationToken.None);

        Assert.That(listed, Has.Count.EqualTo(1));
        Assert.That(listed[0].ClientTaskId, Is.EqualTo("task-good"));
    }

    [Test]
    public async Task ListAsync_TempFileFromInterruptedWrite_Ignored()
    {
        await _store.EnqueueAsync(MakeEntry("task-1", "db1", "c1"), CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(_root, $"task-leftover.json.tmp-{Guid.NewGuid():N}"),
            "half-written");

        var listed = await _store.ListAsync(CancellationToken.None);

        Assert.That(listed, Has.Count.EqualTo(1));
        Assert.That(listed[0].ClientTaskId, Is.EqualTo("task-1"));
    }

    [Test]
    public async Task RemoveAsync_RemovesFile()
    {
        await _store.EnqueueAsync(MakeEntry("task-1", "db1", "c1"), CancellationToken.None);

        await _store.RemoveAsync("task-1", CancellationToken.None);
        var listed = await _store.ListAsync(CancellationToken.None);

        Assert.That(listed, Is.Empty);
        Assert.That(File.Exists(Path.Combine(_root, "task-1.json")), Is.False);
    }

    [Test]
    public async Task RemoveAsync_NonexistentTaskId_DoesNotThrow()
    {
        await _store.RemoveAsync("does-not-exist", CancellationToken.None);

        Assert.Pass();
    }

    [Test]
    public async Task MoveToDeadAsync_MovesFileAndWritesReason()
    {
        await _store.EnqueueAsync(MakeEntry("task-1", "db1", "c1"), CancellationToken.None);

        await _store.MoveToDeadAsync("task-1", "too many retries", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(_root, "task-1.json")), Is.False);
            Assert.That(File.Exists(Path.Combine(_root, "dead", "task-1.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(_root, "dead", "task-1.reason.txt")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(_root, "dead", "task-1.reason.txt")),
                Is.EqualTo("too many retries"));
        });
    }

    [Test]
    public async Task MoveToDeadAsync_RemovesFromList()
    {
        await _store.EnqueueAsync(MakeEntry("task-1", "db1", "c1"), CancellationToken.None);
        await _store.MoveToDeadAsync("task-1", "reason", CancellationToken.None);

        var listed = await _store.ListAsync(CancellationToken.None);

        Assert.That(listed, Is.Empty);
    }

    [Test]
    public async Task MoveToDeadAsync_NonexistentTaskId_Noop()
    {
        await _store.MoveToDeadAsync("does-not-exist", "reason", CancellationToken.None);

        Assert.That(Directory.Exists(Path.Combine(_root, "dead")) &&
                    Directory.GetFiles(Path.Combine(_root, "dead")).Length > 0,
            Is.False);
    }

    [Test]
    public async Task EnqueueAsync_ConcurrentDifferentIds_AllPersisted()
    {
        var ids = Enumerable.Range(0, 20).Select(i => $"task-{i}").ToArray();
        var tasks = ids.Select(id =>
            _store.EnqueueAsync(MakeEntry(id, "db1", "c1"), CancellationToken.None)).ToArray();

        await Task.WhenAll(tasks);

        var listed = await _store.ListAsync(CancellationToken.None);

        Assert.That(listed.Select(e => e.ClientTaskId).OrderBy(x => x).ToList(),
            Is.EqualTo(ids.OrderBy(x => x).ToList()));
    }

    [Test]
    public async Task EnqueueAsync_PreservesAllFields()
    {
        var entry = new OutboxEntry
        {
            SchemaVersion = 1,
            ClientTaskId = "task-1",
            DatabaseName = "payments",
            ConnectionName = "pg-main",
            StorageName = "s3-prod",
            StartedAt = new DateTime(2026, 4, 20, 2, 0, 3, DateTimeKind.Utc),
            BackupAt = new DateTime(2026, 4, 20, 2, 18, 44, DateTimeKind.Utc),
            Status = "success",
            SizeBytes = 812_736_412,
            DurationMs = 1_121_000,
            DumpObjectKey = "payments/2026-04-20_02-00-03/dump.sql.gz.enc",
            ManifestKey = "payments/2026-04-20_02-00-03/manifest.json.gz.enc",
            FilesCount = 1240,
            FilesTotalBytes = 5_123_987,
            NewChunksCount = 87,
            FileBackupError = null,
            ErrorMessage = null,
            QueuedAt = new DateTime(2026, 4, 20, 2, 18, 44, DateTimeKind.Utc),
            AttemptCount = 0,
            ServerRecordId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        };

        await _store.EnqueueAsync(entry, CancellationToken.None);
        var listed = await _store.ListAsync(CancellationToken.None);

        Assert.That(listed, Has.Count.EqualTo(1));
        Assert.That(listed[0], Is.EqualTo(entry));
    }

    private static OutboxEntry MakeEntry(string taskId, string database, string connection) => new()
    {
        ClientTaskId = taskId,
        DatabaseName = database,
        ConnectionName = connection,
        StorageName = "main",
        StartedAt = DateTime.UtcNow,
        BackupAt = DateTime.UtcNow,
        Status = "success",
        QueuedAt = DateTime.UtcNow,
    };
}
