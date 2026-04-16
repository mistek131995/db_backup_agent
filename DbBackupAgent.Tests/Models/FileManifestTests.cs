using System.Text.Json;
using DbBackupAgent.Models;

namespace DbBackupAgent.Tests.Models;

[TestFixture]
public sealed class FileManifestTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Test]
    public void Serialize_UsesCamelCasePropertyNames()
    {
        var manifest = new FileManifest(
            CreatedAtUtc: new DateTime(2026, 4, 17, 14, 30, 0, DateTimeKind.Utc),
            Database: "customers",
            DumpObjectKey: "customers_2026-04-17_14-30-00/dump.sql.gz.enc",
            Files: [new FileEntry("uploads/photo.jpg", 1234567, 1712345678, 420, ["abc", "def"])]);

        var json = JsonSerializer.Serialize(manifest, JsonOptions);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"createdAtUtc\""));
            Assert.That(json, Does.Contain("\"database\""));
            Assert.That(json, Does.Contain("\"dumpObjectKey\""));
            Assert.That(json, Does.Contain("\"files\""));
            Assert.That(json, Does.Contain("\"path\""));
            Assert.That(json, Does.Contain("\"size\""));
            Assert.That(json, Does.Contain("\"mtime\""));
            Assert.That(json, Does.Contain("\"mode\""));
            Assert.That(json, Does.Contain("\"chunks\""));
        });
    }

    [Test]
    public void Serialize_Deserialize_RoundTripsAllFields()
    {
        var original = new FileManifest(
            CreatedAtUtc: new DateTime(2026, 4, 17, 9, 0, 0, DateTimeKind.Utc),
            Database: "orders",
            DumpObjectKey: "orders_2026-04-17_09-00-00/dump.bak.enc",
            Files:
            [
                new FileEntry("a.txt", 100, 1000, 420, ["h1"]),
                new FileEntry("sub/b.bin", 500, 2000, 0, ["h2", "h3"]),
            ]);

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var restored = JsonSerializer.Deserialize<FileManifest>(json, JsonOptions)!;

        Assert.Multiple(() =>
        {
            Assert.That(restored.CreatedAtUtc, Is.EqualTo(original.CreatedAtUtc));
            Assert.That(restored.Database, Is.EqualTo(original.Database));
            Assert.That(restored.DumpObjectKey, Is.EqualTo(original.DumpObjectKey));
            Assert.That(restored.Files, Has.Count.EqualTo(original.Files.Count));
            for (int i = 0; i < original.Files.Count; i++)
            {
                Assert.That(restored.Files[i].Path, Is.EqualTo(original.Files[i].Path));
                Assert.That(restored.Files[i].Size, Is.EqualTo(original.Files[i].Size));
                Assert.That(restored.Files[i].Mtime, Is.EqualTo(original.Files[i].Mtime));
                Assert.That(restored.Files[i].Mode, Is.EqualTo(original.Files[i].Mode));
                Assert.That(restored.Files[i].Chunks, Is.EqualTo(original.Files[i].Chunks));
            }
        });
    }
}
