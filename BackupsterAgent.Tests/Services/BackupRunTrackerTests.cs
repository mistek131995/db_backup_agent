using BackupsterAgent.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupsterAgent.Tests.Services;

[TestFixture]
public sealed class BackupRunTrackerTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"runs-tracker-test-{Guid.NewGuid():N}");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Test]
    public void GetLastRun_NoRecord_ReturnsNull()
    {
        var tracker = CreateTracker();

        Assert.That(tracker.GetLastRun("unknown"), Is.Null);
    }

    [Test]
    public void RecordRun_ThenGetLastRun_ReturnsRecordedValue()
    {
        var tracker = CreateTracker();
        var t = new DateTime(2026, 4, 20, 2, 0, 0, DateTimeKind.Utc);

        tracker.RecordRun("payments", t);

        Assert.That(tracker.GetLastRun("payments"), Is.EqualTo(t));
    }

    [Test]
    public void RecordRun_OlderThanExisting_DoesNotOverwrite()
    {
        var tracker = CreateTracker();
        var newer = new DateTime(2026, 4, 20, 3, 0, 0, DateTimeKind.Utc);
        var older = newer.AddHours(-1);

        tracker.RecordRun("payments", newer);
        tracker.RecordRun("payments", older);

        Assert.That(tracker.GetLastRun("payments"), Is.EqualTo(newer));
    }

    [Test]
    public void RecordRun_NewerThanExisting_Overwrites()
    {
        var tracker = CreateTracker();
        var older = new DateTime(2026, 4, 20, 2, 0, 0, DateTimeKind.Utc);
        var newer = older.AddHours(1);

        tracker.RecordRun("payments", older);
        tracker.RecordRun("payments", newer);

        Assert.That(tracker.GetLastRun("payments"), Is.EqualTo(newer));
    }

    [Test]
    public void RecordRun_DifferentDatabases_AreIsolated()
    {
        var tracker = CreateTracker();
        var t1 = new DateTime(2026, 4, 20, 2, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddHours(1);

        tracker.RecordRun("payments", t1);
        tracker.RecordRun("orders", t2);

        Assert.Multiple(() =>
        {
            Assert.That(tracker.GetLastRun("payments"), Is.EqualTo(t1));
            Assert.That(tracker.GetLastRun("orders"), Is.EqualTo(t2));
        });
    }

    [Test]
    public void RecordRun_StateSurvivesRecreation()
    {
        var t = new DateTime(2026, 4, 20, 2, 0, 0, DateTimeKind.Utc);

        var first = CreateTracker();
        first.RecordRun("payments", t);

        var second = CreateTracker();

        Assert.That(second.GetLastRun("payments"), Is.EqualTo(t));
    }

    [Test]
    public void RecordRun_StateSurvivesRecreation_MultipleDatabases()
    {
        var baseT = new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc);

        var first = CreateTracker();
        first.RecordRun("db1", baseT);
        first.RecordRun("db2", baseT.AddMinutes(5));
        first.RecordRun("db3", baseT.AddMinutes(10));

        var second = CreateTracker();

        Assert.Multiple(() =>
        {
            Assert.That(second.GetLastRun("db1"), Is.EqualTo(baseT));
            Assert.That(second.GetLastRun("db2"), Is.EqualTo(baseT.AddMinutes(5)));
            Assert.That(second.GetLastRun("db3"), Is.EqualTo(baseT.AddMinutes(10)));
        });
    }

    [Test]
    public void RecordRun_ConcurrentWritesDifferentDatabases_AllPersisted()
    {
        var tracker = CreateTracker();
        var t = new DateTime(2026, 4, 20, 2, 0, 0, DateTimeKind.Utc);

        var tasks = Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => tracker.RecordRun($"db-{i}", t.AddMinutes(i))))
            .ToArray();

        Task.WaitAll(tasks);

        var recreated = CreateTracker();
        for (var i = 0; i < 20; i++)
        {
            Assert.That(recreated.GetLastRun($"db-{i}"), Is.EqualTo(t.AddMinutes(i)),
                $"db-{i} should be persisted");
        }
    }

    private BackupRunTracker CreateTracker()
    {
        var store = new RunStateStore(_root, NullLogger<RunStateStore>.Instance);
        return new BackupRunTracker(store, NullLogger<BackupRunTracker>.Instance);
    }
}
