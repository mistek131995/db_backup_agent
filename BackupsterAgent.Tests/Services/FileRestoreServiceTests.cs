using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackupsterAgent.Configuration;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Providers.Upload;
using BackupsterAgent.Services;
using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Common.Security;
using BackupsterAgent.Services.Restore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Tests.Services;

[TestFixture]
public sealed class FileRestoreServiceTests
{
    private const string ManifestKey = "db_2026-01-01_00-00-00/manifest.json.enc";
    private const int SafeMode = 0x1A4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private string _tempRoot = null!;
    private string _landingDir = null!;
    private byte[] _key = null!;
    private EncryptionService _encryption = null!;
    private FakeUploadProvider _upload = null!;
    private FileRestoreService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "dbbackup-file-restore-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _landingDir = Path.Combine(_tempRoot, "landing");

        _key = RandomNumberGenerator.GetBytes(32);
        _encryption = new EncryptionService(
            Options.Create(new EncryptionSettings { Key = Convert.ToBase64String(_key) }),
            NullLogger<EncryptionService>.Instance);

        _upload = new FakeUploadProvider();
        var restoreSettings = Options.Create(new RestoreSettings
        {
            FileRestoreBasePath = _landingDir,
            TempPath = _tempRoot,
        });
        var manifestStore = new ManifestStore(
            _encryption,
            restoreSettings,
            NullLoggerFactory.Instance,
            NullLogger<ManifestStore>.Instance);
        _service = new FileRestoreService(
            _encryption,
            manifestStore,
            restoreSettings,
            NullLogger<FileRestoreService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Test]
    public async Task RunAsync_EmptyManifest_ReturnsSuccessZeroAndSkipsChunkDownloads()
    {
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", []));

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Success));
            Assert.That(result.FilesRestoredCount, Is.Zero);
            Assert.That(_upload.ChunkDownloads, Is.Zero, "no chunks must be downloaded for empty manifest");
        });
    }

    [Test]
    public async Task RunAsync_SingleFileSingleChunk_RoundTripRestoresOriginalBytes()
    {
        var content = RandomNumberGenerator.GetBytes(4096);
        var sha = StoreChunk(content);
        var entry = new FileEntry("data.bin", content.Length, 0, SafeMode, [sha]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [entry]));

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Success));
        var restored = await File.ReadAllBytesAsync(Path.Combine(_tempRoot, "data.bin"));
        Assert.That(restored, Is.EqualTo(content));
    }

    [Test]
    public async Task RunAsync_MultiChunkFile_AssembledInManifestOrderNotDownloadOrder()
    {
        var c1 = RandomNumberGenerator.GetBytes(100);
        var c2 = RandomNumberGenerator.GetBytes(100);
        var c3 = RandomNumberGenerator.GetBytes(100);
        var sha1 = StoreChunk(c1);
        var sha2 = StoreChunk(c2);
        var sha3 = StoreChunk(c3);

        // Manifest order: c2 → c1 → c3. Assembled file must follow this order.
        var expected = new byte[300];
        Buffer.BlockCopy(c2, 0, expected, 0, 100);
        Buffer.BlockCopy(c1, 0, expected, 100, 100);
        Buffer.BlockCopy(c3, 0, expected, 200, 100);

        var entry = new FileEntry("out.bin", expected.Length, 0, SafeMode, [sha2, sha1, sha3]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [entry]));

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Success));
        var restored = await File.ReadAllBytesAsync(Path.Combine(_tempRoot, "out.bin"));
        Assert.That(restored, Is.EqualTo(expected));
    }

    [Test]
    public async Task RunAsync_SizeMismatchWithManifest_DeletesTempAndReportsPartial()
    {
        var content = new byte[100];
        var sha = StoreChunk(content);
        // Manifest claims 999 bytes while chunk is 100 — mismatch.
        var entry = new FileEntry("bad.bin", 999, 0, SafeMode, [sha]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [entry]));

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        var target = Path.Combine(_tempRoot, "bad.bin");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Partial));
            Assert.That(result.FilesFailedCount, Is.EqualTo(1));
            Assert.That(File.Exists(target), Is.False, "target must not exist when size check fails");
            Assert.That(File.Exists(target + ".restore-tmp"), Is.False, ".restore-tmp must be cleaned up");
            Assert.That(result.ErrorMessage, Does.Contain("bad.bin"));
            Assert.That(result.ErrorMessage, Does.Contain("размер"));
            Assert.That(result.ErrorMessage, Does.Contain("не совпадает"));
        });
    }

    [Test]
    public async Task RunAsync_ExceptionMidAssembly_DeletesTempAndDoesNotCreateTarget()
    {
        var c1 = RandomNumberGenerator.GetBytes(100);
        var sha1 = StoreChunk(c1);
        var missingSha = "deadbeef" + new string('0', 56);
        var entry = new FileEntry("f.bin", 200, 0, SafeMode, [sha1, missingSha]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [entry]));

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        var target = Path.Combine(_tempRoot, "f.bin");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Partial));
            Assert.That(result.FilesFailedCount, Is.EqualTo(1));
            Assert.That(File.Exists(target), Is.False);
            Assert.That(File.Exists(target + ".restore-tmp"), Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("f.bin"));
            Assert.That(result.ErrorMessage, Does.Contain("отсутствует чанк"));
        });
    }

    [Test]
    public async Task RunAsync_TargetAlreadyExists_IsOverwritten()
    {
        var newContent = RandomNumberGenerator.GetBytes(256);
        var sha = StoreChunk(newContent);
        var entry = new FileEntry("f.bin", newContent.Length, 0, SafeMode, [sha]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [entry]));

        var target = Path.Combine(_tempRoot, "f.bin");
        await File.WriteAllBytesAsync(target, new byte[] { 9, 9, 9 });

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Success));
        Assert.That(await File.ReadAllBytesAsync(target), Is.EqualTo(newContent));
    }

    [Test]
    public async Task RunAsync_TwoConsecutiveRestoresOfSameRelPath_SecondContentWins()
    {
        var contentV1 = RandomNumberGenerator.GetBytes(2048);
        var contentV2 = RandomNumberGenerator.GetBytes(3072);
        Assert.That(contentV1, Is.Not.EqualTo(contentV2));

        var sha1 = StoreChunk(contentV1);
        var entryV1 = new FileEntry("excel.xlsx", contentV1.Length, 0, SafeMode, [sha1]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "fileset", "", [entryV1]));

        var firstResult = await _service.RunAsync(
            ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.That(firstResult.Status, Is.EqualTo(RestoreFilesStatus.Success), "first restore must succeed");
        var target = Path.Combine(_tempRoot, "excel.xlsx");
        Assert.That(await File.ReadAllBytesAsync(target), Is.EqualTo(contentV1),
            "after first restore, target must hold v1 bytes");

        var sha2 = StoreChunk(contentV2);
        var entryV2 = new FileEntry("excel.xlsx", contentV2.Length, 0, SafeMode, [sha2]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "fileset", "", [entryV2]));

        var secondResult = await _service.RunAsync(
            ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(secondResult.Status, Is.EqualTo(RestoreFilesStatus.Success),
                "second restore must succeed");
            Assert.That(File.Exists(target + ".restore-tmp"), Is.False,
                ".restore-tmp from second restore must be cleaned up");
        });
        Assert.That(await File.ReadAllBytesAsync(target), Is.EqualTo(contentV2),
            "after second restore, target must hold v2 bytes — overwrite of pre-existing same-relPath file");
    }

    [Test]
    public async Task RunAsync_WindowsDriveLetterInEntryPath_RejectedAsUnsafe()
    {
        var content = RandomNumberGenerator.GetBytes(50);
        var sha = StoreChunk(content);
        var entry = new FileEntry(@"C:\data\file.txt", content.Length, 0, SafeMode, [sha]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [entry]));

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Partial));
            Assert.That(result.FilesFailedCount, Is.EqualTo(1));
            Assert.That(result.ErrorMessage, Does.Contain("буквой диска"));
        });
    }

    [Test]
    public async Task RunAsync_UnixAbsoluteEntryPath_RejectedAsUnsafe()
    {
        var content = RandomNumberGenerator.GetBytes(50);
        var sha = StoreChunk(content);
        var entry = new FileEntry("/var/log/app.log", content.Length, 0, SafeMode, [sha]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [entry]));

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Partial));
            Assert.That(result.FilesFailedCount, Is.EqualTo(1));
            Assert.That(result.ErrorMessage, Does.Contain("абсолютный путь"));
        });
    }

    [Test]
    public async Task RunAsync_ParentTraversalInEntryPath_RejectedAsUnsafe()
    {
        var content = RandomNumberGenerator.GetBytes(50);
        var sha = StoreChunk(content);
        var entry = new FileEntry("../../etc/passwd", content.Length, 0, SafeMode, [sha]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [entry]));

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Partial));
            Assert.That(result.FilesFailedCount, Is.EqualTo(1));
            Assert.That(result.ErrorMessage, Does.Contain("выходит за пределы"));
        });
    }

    [Test]
    public async Task RunAsync_MixedSlashesInEntryPath_NormalizedToPlatformSeparator()
    {
        var content = RandomNumberGenerator.GetBytes(50);
        var sha = StoreChunk(content);
        var entry = new FileEntry("sub/other\\leaf.bin", content.Length, 0, SafeMode, [sha]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [entry]));

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Success));
        var expected = Path.Combine(_tempRoot, "sub", "other", "leaf.bin");
        Assert.That(File.Exists(expected), Is.True, $"expected file at {expected}");
    }

    [Test]
    public async Task RunAsync_NullTargetFileRoot_WritesToAgentLandingZone()
    {
        var content = RandomNumberGenerator.GetBytes(50);
        var sha = StoreChunk(content);
        var entry = new FileEntry("sub/file.bin", content.Length, 0, SafeMode, [sha]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [entry]));

        var result = await _service.RunAsync(ManifestKey, targetFileRoot: null, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        var expected = Path.Combine(_landingDir, "sub", "file.bin");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Success));
            Assert.That(File.Exists(expected), Is.True, $"expected file at {expected}");
        });
        Assert.That(await File.ReadAllBytesAsync(expected), Is.EqualTo(content));
    }

    [Test]
    public async Task RunAsync_NullTargetFileRoot_ClearsLandingZoneBeforeRestore()
    {
        Directory.CreateDirectory(_landingDir);
        var stale = Path.Combine(_landingDir, "stale-from-previous.bin");
        await File.WriteAllBytesAsync(stale, [1, 2, 3]);

        var content = RandomNumberGenerator.GetBytes(20);
        var sha = StoreChunk(content);
        var entry = new FileEntry("fresh.bin", content.Length, 0, SafeMode, [sha]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [entry]));

        var result = await _service.RunAsync(ManifestKey, targetFileRoot: null, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Success));
            Assert.That(File.Exists(stale), Is.False, "stale landing-zone files must be wiped before restore");
            Assert.That(File.Exists(Path.Combine(_landingDir, "fresh.bin")), Is.True);
        });
    }

    [Test]
    public async Task RunAsync_ExplicitTargetFileRoot_DoesNotClearTargetDir()
    {
        var keep = Path.Combine(_tempRoot, "user-kept.bin");
        await File.WriteAllBytesAsync(keep, [7, 7, 7]);

        var content = RandomNumberGenerator.GetBytes(20);
        var sha = StoreChunk(content);
        var entry = new FileEntry("restored.bin", content.Length, 0, SafeMode, [sha]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [entry]));

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Success));
            Assert.That(File.Exists(keep), Is.True, "explicit targetFileRoot must not be wiped");
            Assert.That(File.Exists(Path.Combine(_tempRoot, "restored.bin")), Is.True);
        });
    }

    [Test]
    public async Task RunAsync_OneChunkMissing_OtherFilesRestoredAndPartialReported()
    {
        var good1 = RandomNumberGenerator.GetBytes(50);
        var good2 = RandomNumberGenerator.GetBytes(50);
        var missing = "deadbeef" + new string('0', 56);
        var sha1 = StoreChunk(good1);
        var sha2 = StoreChunk(good2);

        var entries = new List<FileEntry>
        {
            new("a.bin", good1.Length, 0, SafeMode, [sha1]),
            new("b.bin", 50, 0, SafeMode, [missing]),
            new("c.bin", good2.Length, 0, SafeMode, [sha2]),
        };
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", entries));

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Partial));
            Assert.That(result.FilesRestoredCount, Is.EqualTo(2));
            Assert.That(result.FilesFailedCount, Is.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(_tempRoot, "a.bin")), Is.True);
            Assert.That(File.Exists(Path.Combine(_tempRoot, "b.bin")), Is.False);
            Assert.That(File.Exists(Path.Combine(_tempRoot, "c.bin")), Is.True);
            Assert.That(result.ErrorMessage, Does.Contain("b.bin"));
            Assert.That(result.ErrorMessage, Does.Contain("отсутствует чанк в хранилище"));
        });
    }

    [Test]
    public async Task RunAsync_TamperedChunk_ErrorMessageMentionsDecryptFailure()
    {
        var content = RandomNumberGenerator.GetBytes(256);
        var sha = StoreChunk(content);
        _upload.TamperBytes($"chunks/{sha}");
        var entry = new FileEntry("f.bin", content.Length, 0, SafeMode, [sha]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [entry]));

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Partial));
            Assert.That(result.FilesFailedCount, Is.EqualTo(1));
            Assert.That(result.ErrorMessage, Does.Contain("ошибка расшифровки чанка"));
        });
    }

    [Test]
    public async Task RunAsync_MoreThan20Failures_TruncatesListAndAppendsRemainderCount()
    {
        var missing = "deadbeef" + new string('0', 56);
        var entries = new List<FileEntry>();
        for (int i = 0; i < 25; i++)
            entries.Add(new FileEntry($"f{i}.bin", 50, 0, SafeMode, [missing]));
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", entries));

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Partial));
            Assert.That(result.FilesFailedCount, Is.EqualTo(25));
            Assert.That(result.ErrorMessage, Does.Contain("и ещё 5 ошибок"));
        });
    }

    [Test]
    public async Task RunAsync_LongPaths_ErrorMessageStaysWithin2000CharsAndEndsWithMarker()
    {
        var missing = "deadbeef" + new string('0', 56);
        var longPart = new string('x', 400);
        var entries = new List<FileEntry>();
        for (int i = 0; i < 20; i++)
            entries.Add(new FileEntry($"{longPart}-{i}.bin", 50, 0, SafeMode, [missing]));
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", entries));

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorMessage, Is.Not.Null);
            Assert.That(result.ErrorMessage!.Length, Is.LessThanOrEqualTo(2000));
            Assert.That(result.ErrorMessage, Does.EndWith("(обрезано, см. логи агента)"));
        });
    }

    [Test]
    public async Task RunAsync_ManifestAuthTagMismatch_FailedWithKeyHintAndNoChunkDownloads()
    {
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [
            new FileEntry("a.bin", 10, 0, SafeMode, ["sha"])
        ]));
        _upload.TamperBytes(ManifestKey);

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Failed));
            Assert.That(result.ErrorMessage, Does.Contain("EncryptionKey"));
            Assert.That(_upload.ChunkDownloads, Is.Zero, "chunks must not be touched when manifest decrypt fails");
        });
    }

    [Test]
    public async Task RunAsync_BrokenManifestJson_FailedWithJsonMessage()
    {
        var garbage = Encoding.UTF8.GetBytes("{not a manifest");
        var encrypted = _encryption.Encrypt(garbage, Encoding.UTF8.GetBytes(ManifestKey));
        _upload.SetBytes(ManifestKey, encrypted);

        var result = await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RestoreFilesStatus.Failed));
            Assert.That(result.ErrorMessage, Does.Contain("JSON"));
        });
    }

    [Test]
    public void RunAsync_CancellationPreAcquired_Propagates()
    {
        var c1 = RandomNumberGenerator.GetBytes(50);
        var c2 = RandomNumberGenerator.GetBytes(50);
        var sha1 = StoreChunk(c1);
        var sha2 = StoreChunk(c2);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key",
        [
            new FileEntry("a.bin", c1.Length, 0, SafeMode, [sha1]),
            new FileEntry("b.bin", c2.Length, 0, SafeMode, [sha2]),
        ]));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), cts.Token));
    }

    [Test]
    public async Task RunAsync_AppliesMtimeFromManifestEntry()
    {
        var mtime = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var content = RandomNumberGenerator.GetBytes(50);
        var sha = StoreChunk(content);
        var entry = new FileEntry("m.bin", content.Length, mtime, SafeMode, [sha]);
        StoreManifest(new FileManifest(DateTime.UtcNow, "db", "dump.key", [entry]));

        await _service.RunAsync(ManifestKey, _tempRoot, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        var actual = new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(_tempRoot, "m.bin")))
            .ToUnixTimeSeconds();
        // Tolerate coarse-granularity filesystems (FAT32: 2 s, some SMB/NFS mounts).
        Assert.That(actual, Is.EqualTo(mtime).Within(2L));
    }

    private string StoreChunk(byte[] plaintext)
    {
        var shaBytes = SHA256.HashData(plaintext);
        var sha = Convert.ToHexString(shaBytes).ToLowerInvariant();
        _upload.SetBytes($"chunks/{sha}", _encryption.Encrypt(plaintext, shaBytes));
        return sha;
    }

    private void StoreManifest(FileManifest manifest)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        _upload.SetBytes(ManifestKey, _encryption.Encrypt(json, Encoding.UTF8.GetBytes(ManifestKey)));
    }

    private sealed class FakeUploadProvider : IUploadProvider
    {
        private readonly Dictionary<string, byte[]> _bytes = [];

        public int ChunkDownloads { get; private set; }

        public void SetBytes(string key, byte[] data) => _bytes[key] = data;

        public void TamperBytes(string key)
        {
            var data = _bytes[key];
            data[data.Length / 2] ^= 0xFF;
        }

        public Task<byte[]> DownloadBytesAsync(string objectKey, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (objectKey.StartsWith("chunks/", StringComparison.Ordinal))
                ChunkDownloads++;

            if (!_bytes.TryGetValue(objectKey, out var data))
                throw new FileNotFoundException($"Fake S3: object '{objectKey}' not found.", objectKey);

            return Task.FromResult(data);
        }

        public Task<string> UploadAsync(string filePath, string folder, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException("FileRestoreService must not call UploadAsync");

        public Task UploadBytesAsync(byte[] content, string objectKey, CancellationToken ct) =>
            throw new NotSupportedException("FileRestoreService must not call UploadBytesAsync");

        public Task<bool> ExistsAsync(string objectKey, CancellationToken ct) =>
            throw new NotSupportedException("FileRestoreService must not call ExistsAsync");

        public Task DownloadAsync(string objectKey, string localPath, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException("FileRestoreService must not call DownloadAsync");

        public IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken ct) =>
            throw new NotSupportedException("FileRestoreService must not call ListAsync");

        public Task DeleteAsync(string objectKey, CancellationToken ct) =>
            throw new NotSupportedException("FileRestoreService must not call DeleteAsync");
    }
}
