using DbBackupAgent.Configuration;
using DbBackupAgent.Services;

namespace DbBackupAgent.Tests.Services;

[TestFixture]
public sealed class ConnectionResolverTests
{
    [Test]
    public void Resolve_KnownName_ReturnsConnection()
    {
        var conn = new ConnectionConfig
        {
            Name = "main-pg",
            DatabaseType = "Postgres",
            Host = "db.internal",
            Port = 5432,
            Username = "u",
            Password = "p",
        };
        var resolver = new ConnectionResolver([conn]);

        var result = resolver.Resolve("main-pg");

        Assert.That(result, Is.SameAs(conn));
    }

    [Test]
    public void Resolve_UnknownName_ThrowsWithAvailableNames()
    {
        var resolver = new ConnectionResolver(
        [
            new ConnectionConfig { Name = "main-pg" },
            new ConnectionConfig { Name = "reporting-mssql" },
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve("missing"));
        Assert.That(ex!.Message, Does.Contain("'missing'"));
        Assert.That(ex.Message, Does.Contain("main-pg"));
        Assert.That(ex.Message, Does.Contain("reporting-mssql"));
    }

    [Test]
    public void Resolve_EmptyList_ThrowsWithNoneHint()
    {
        var resolver = new ConnectionResolver([]);

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve("anything"));
        Assert.That(ex!.Message, Does.Contain("(none)"));
    }

    [Test]
    public void Ctor_DuplicateNames_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ConnectionResolver(
        [
            new ConnectionConfig { Name = "dup" },
            new ConnectionConfig { Name = "dup" },
        ]));

        Assert.That(ex!.Message, Does.Contain("Duplicate"));
        Assert.That(ex.Message, Does.Contain("'dup'"));
    }

    [Test]
    public void Ctor_EmptyName_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new ConnectionResolver(
        [
            new ConnectionConfig { Name = "" },
        ]));
    }

    [Test]
    public void Ctor_WhitespaceName_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new ConnectionResolver(
        [
            new ConnectionConfig { Name = "   " },
        ]));
    }

    [Test]
    public void TryResolve_KnownName_ReturnsTrue()
    {
        var resolver = new ConnectionResolver(
        [
            new ConnectionConfig { Name = "main-pg" },
        ]);

        var ok = resolver.TryResolve("main-pg", out var result);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(result.Name, Is.EqualTo("main-pg"));
        });
    }

    [Test]
    public void TryResolve_UnknownName_ReturnsFalse()
    {
        var resolver = new ConnectionResolver([]);

        var ok = resolver.TryResolve("missing", out _);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void Names_ExposesAllConfiguredNames()
    {
        var resolver = new ConnectionResolver(
        [
            new ConnectionConfig { Name = "a" },
            new ConnectionConfig { Name = "b" },
        ]);

        Assert.That(resolver.Names, Is.EquivalentTo(new[] { "a", "b" }));
    }

    [Test]
    public void ResolutionIsCaseSensitive()
    {
        var resolver = new ConnectionResolver(
        [
            new ConnectionConfig { Name = "Main-PG" },
        ]);

        Assert.Throws<InvalidOperationException>(() => resolver.Resolve("main-pg"));
        Assert.DoesNotThrow(() => resolver.Resolve("Main-PG"));
    }
}
