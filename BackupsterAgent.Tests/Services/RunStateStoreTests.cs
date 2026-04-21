using BackupsterAgent.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupsterAgent.Tests.Services;

[TestFixture]
public sealed class RunStateStoreTests
{
    private string _root = null!;
    private RunStateStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"runs-test-{Guid.NewGuid():N}");
        _store = new RunStateStore(_root, NullLogger<RunStateStore>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Test]
    public void LoadAll_EmptyDirectory_ReturnsEmpty()
    {
        Directory.CreateDirectory(_root);

        var result = _store.LoadAll();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void LoadAll_NonexistentDirectory_ReturnsEmpty()
    {
        var result = _store.LoadAll();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Write_ThenLoadAll_RoundTrips()
    {
        var t = new DateTime(2026, 4, 20, 2, 0, 0, DateTimeKind.Utc);

        _store.Write("payments", t);
        var result = _store.LoadAll();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result["payments"], Is.EqualTo(t));
    }

    [Test]
    public void Write_MultipleDatabases_AllLoaded()
    {
        var t = new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc);

        _store.Write("payments", t);
        _store.Write("orders", t.AddMinutes(5));
        _store.Write("inventory", t.AddMinutes(10));

        var result = _store.LoadAll();

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(3));
            Assert.That(result["payments"], Is.EqualTo(t));
            Assert.That(result["orders"], Is.EqualTo(t.AddMinutes(5)));
            Assert.That(result["inventory"], Is.EqualTo(t.AddMinutes(10)));
        });
    }

    [Test]
    public void Write_SameDatabase_OverwritesWithLatest()
    {
        var t1 = new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddHours(1);

        _store.Write("payments", t1);
        _store.Write("payments", t2);

        var result = _store.LoadAll();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result["payments"], Is.EqualTo(t2));
    }

    [Test]
    public void Write_EmptyDatabaseName_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _store.Write(string.Empty, DateTime.UtcNow));
    }

    [Test]
    public void Write_UnsafeDatabaseName_WritesSuccessfully()
    {
        var t = DateTime.UtcNow;

        _store.Write("../../../etc/passwd", t);
        var result = _store.LoadAll();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result["../../../etc/passwd"], Is.EqualTo(t));

        Assert.That(Directory.EnumerateFiles(_root).All(p =>
            Path.GetDirectoryName(p)!.Equals(_root, StringComparison.OrdinalIgnoreCase)),
            "file should stay within the runs directory");
    }

    [Test]
    public void LoadAll_CorruptJsonFile_SkipsAndContinues()
    {
        _store.Write("payments", new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "garbage.json"), "{not valid");

        var result = _store.LoadAll();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.ContainsKey("payments"), Is.True);
    }

    [Test]
    public void LoadAll_NullDeserialized_Skipped()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "null.json"), "null");

        var result = _store.LoadAll();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Write_DifferentNamesWithSameSanitizedPrefix_DoNotCollide()
    {
        var t1 = new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddHours(1);

        _store.Write("db/one", t1);
        _store.Write("db-one", t2);

        var result = _store.LoadAll();

        Assert.Multiple(() =>
        {
            Assert.That(result["db/one"], Is.EqualTo(t1));
            Assert.That(result["db-one"], Is.EqualTo(t2));
        });
    }
}
