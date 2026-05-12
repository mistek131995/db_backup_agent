using System.Security.Cryptography;
using BackupsterAgent.Configuration;
using BackupsterAgent.Providers.Upload;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupsterAgent.IntegrationTests.Storage;

[TestFixture]
[Category("Integration")]
public sealed class WebDavUploadProviderIntegrationTests
{
    private WebDavUploadProvider _provider = null!;
    private WebDavSettings _settings = null!;
    private CancellationTokenSource _cts = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Assume.That(
            IntegrationConfig.TryGetWebDavSettings(out var settings),
            Is.True,
            "WebDav:* not configured; set via dotnet user-secrets or BACKUPSTER_INTEGRATION_WEBDAV__* env vars.");

        var prefix = IntegrationConfig.MakeUniquePrefix(nameof(WebDavUploadProviderIntegrationTests));
        _settings = new WebDavSettings
        {
            BaseUrl = settings.BaseUrl,
            Username = settings.Username,
            Password = settings.Password,
            RemotePath = CombinePath(settings.RemotePath, prefix),
        };
        _provider = new WebDavUploadProvider(_settings, NullLogger<WebDavUploadProvider>.Instance);
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_provider is not null)
        {
            try
            {
                await IntegrationConfig.CleanupPrefixAsync(_provider, string.Empty, CancellationToken.None);
            }
            catch (Exception ex)
            {
                TestContext.Progress.WriteLine($"WebDAV cleanup failed: {ex.Message}");
            }
            _provider.Dispose();
        }
        _cts?.Dispose();
    }

    [Test]
    public async Task UploadBytes_ThenExists_ReturnsTrue()
    {
        var key = $"exists-positive-{Guid.NewGuid():N}";
        var content = RandomNumberGenerator.GetBytes(256);

        await _provider.UploadBytesAsync(content, key, _cts.Token);
        var exists = await _provider.ExistsAsync(key, _cts.Token);

        Assert.That(exists, Is.True);
    }

    [Test]
    public async Task Exists_OnMissingKey_ReturnsFalse()
    {
        var exists = await _provider.ExistsAsync($"missing-{Guid.NewGuid():N}", _cts.Token);
        Assert.That(exists, Is.False);
    }

    [TestCase(0)]
    [TestCase(4 * 1024)]
    [TestCase(4 * 1024 * 1024)]
    public async Task UploadBytes_DownloadBytes_RoundTrip(int size)
    {
        var key = $"roundtrip-{size}-{Guid.NewGuid():N}";
        var content = size == 0 ? Array.Empty<byte>() : RandomNumberGenerator.GetBytes(size);

        await _provider.UploadBytesAsync(content, key, _cts.Token);
        var downloaded = await _provider.DownloadBytesAsync(key, _cts.Token);

        Assert.That(downloaded, Is.EqualTo(content));
    }

    [Test]
    public void DownloadBytes_OnMissingKey_ThrowsFileNotFound()
    {
        Assert.ThrowsAsync<FileNotFoundException>(
            async () => await _provider.DownloadBytesAsync($"missing-{Guid.NewGuid():N}", _cts.Token));
    }

    [Test]
    public async Task UploadAsync_File_ThenGetObjectSize_ReturnsSize()
    {
        var content = RandomNumberGenerator.GetBytes(1024);
        var fileName = $"upload-{Guid.NewGuid():N}.bin";
        var localPath = Path.Combine(Path.GetTempPath(), fileName);
        await File.WriteAllBytesAsync(localPath, content, _cts.Token);

        try
        {
            await _provider.UploadAsync(localPath, "upload-file", progress: null, _cts.Token);
            var key = $"upload-file/{fileName}";
            var size = await _provider.GetObjectSizeAsync(key, _cts.Token);

            Assert.That(size, Is.EqualTo(content.Length));
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [Test]
    public async Task DownloadAsync_File_PreservesContentAndCleansTmp()
    {
        var content = RandomNumberGenerator.GetBytes(2048);
        var key = $"download-file-{Guid.NewGuid():N}";
        await _provider.UploadBytesAsync(content, key, _cts.Token);

        var localPath = Path.Combine(Path.GetTempPath(), $"backupster-itest-{Guid.NewGuid():N}.bin");
        var tmpPath = localPath + ".download-tmp";

        try
        {
            await _provider.DownloadAsync(key, localPath, progress: null, _cts.Token);
            var downloaded = await File.ReadAllBytesAsync(localPath, _cts.Token);

            Assert.Multiple(() =>
            {
                Assert.That(downloaded, Is.EqualTo(content));
                Assert.That(File.Exists(tmpPath), Is.False, "leftover .download-tmp must be removed");
            });
        }
        finally
        {
            if (File.Exists(localPath)) File.Delete(localPath);
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    [Test]
    public async Task ListAsync_WithPrefix_ReturnsOnlyMatching()
    {
        var sentinel = $"list-prefix-{Guid.NewGuid():N}";
        var inChunks = new[] { $"{sentinel}/chunks/a", $"{sentinel}/chunks/b" };
        var inManifests = new[] { $"{sentinel}/manifests/c", $"{sentinel}/manifests/d" };

        foreach (var k in inChunks.Concat(inManifests))
            await _provider.UploadBytesAsync([1], k, _cts.Token);

        var keys = new List<string>();
        await foreach (var obj in _provider.ListAsync($"{sentinel}/chunks/", _cts.Token))
            keys.Add(obj.Key);

        Assert.That(keys, Is.EquivalentTo(inChunks));
    }

    [Test]
    public async Task ListAsync_RecursivelyTraversesSubdirectories()
    {
        var sentinel = $"list-recursive-{Guid.NewGuid():N}";
        var expected = new[]
        {
            $"{sentinel}/a/file1",
            $"{sentinel}/b/c/file2",
            $"{sentinel}/d/e/f/file3",
        };

        foreach (var k in expected)
            await _provider.UploadBytesAsync([7], k, _cts.Token);

        var found = new List<StorageObject>();
        await foreach (var obj in _provider.ListAsync($"{sentinel}/", _cts.Token))
            found.Add(obj);

        Assert.Multiple(() =>
        {
            Assert.That(found.Select(o => o.Key), Is.EquivalentTo(expected));
            Assert.That(found, Has.All.With.Property("LastModifiedUtc").With.Property("Kind").EqualTo(DateTimeKind.Utc));
            Assert.That(found, Has.All.With.Property("Size").EqualTo(1L));
        });
    }

    [Test]
    public async Task UploadBytes_DeeplyNestedKey_CreatesParentDirectories()
    {
        var key = $"a/b/c/d/deep-{Guid.NewGuid():N}";
        await _provider.UploadBytesAsync([9, 9, 9], key, _cts.Token);

        Assert.That(await _provider.ExistsAsync(key, _cts.Token), Is.True);
    }

    [Test]
    public void DeleteAsync_OnMissingKey_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(
            async () => await _provider.DeleteAsync($"missing-{Guid.NewGuid():N}", _cts.Token));
    }

    [Test]
    public void UploadBytes_WithWrongPassword_ThrowsUnauthorized()
    {
        var bad = new WebDavSettings
        {
            BaseUrl = _settings.BaseUrl,
            Username = _settings.Username,
            Password = "wrong-on-purpose",
            RemotePath = _settings.RemotePath,
        };
        using var badProvider = new WebDavUploadProvider(bad, NullLogger<WebDavUploadProvider>.Instance);

        Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await badProvider.UploadBytesAsync([1], "auth-check", _cts.Token));
    }

    private static string CombinePath(string left, string right)
    {
        var l = left.TrimEnd('/');
        var r = right.TrimStart('/');
        return string.IsNullOrEmpty(l) ? "/" + r : $"{l}/{r}";
    }
}
