using System.Security.Cryptography;
using DbBackupAgent.Enums;
using DbBackupAgent.Services;
using DbBackupAgent.Services.Backup;
using DbBackupAgent.Services.Common;
using DbBackupAgent.Services.Upload;
using DbBackupAgent.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbBackupAgent.Tests.Services;

[TestFixture]
public sealed class FileBackupServiceTests
{
    private string _tempRoot = null!;
    private byte[] _encryptionKey = null!;
    private EncryptionService _encryption = null!;
    private ContentDefinedChunker _chunker = null!;
    private FakeUploadService _uploader = null!;
    private FileBackupService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "dbbackup-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);

        _encryptionKey = RandomNumberGenerator.GetBytes(32);
        var encSettings = Options.Create(new EncryptionSettings { Key = Convert.ToBase64String(_encryptionKey) });
        _encryption = new EncryptionService(encSettings, NullLogger<EncryptionService>.Instance);

        _chunker = new ContentDefinedChunker();
        _uploader = new FakeUploadService();

        _service = new FileBackupService(_chunker, _encryption, NullLogger<FileBackupService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { }
    }

    [Test]
    public async Task CaptureAsync_EmptyFilePaths_ReturnsEmptyManifest()
    {
        var result = await _service.CaptureAsync([], _uploader, TestHelpers.NullReporter<BackupStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Manifest.Files, Is.Empty);
            Assert.That(result.NewChunksCount, Is.Zero);
            Assert.That(_uploader.UploadCalls, Is.Zero);
        });
    }

    [Test]
    public async Task CaptureAsync_NonExistentPath_SkipsAndContinues()
    {
        var existing = Path.Combine(_tempRoot, "real");
        Directory.CreateDirectory(existing);
        await File.WriteAllBytesAsync(Path.Combine(existing, "a.bin"), new byte[] { 1, 2, 3 });

        var missing = Path.Combine(_tempRoot, "does-not-exist");

        var result = await _service.CaptureAsync([missing, existing], _uploader, TestHelpers.NullReporter<BackupStage>(), CancellationToken.None);

        Assert.That(result.Manifest.Files, Has.Count.EqualTo(1));
        Assert.That(result.Manifest.Files[0].Path, Is.EqualTo("a.bin"));
    }

    [Test]
    public async Task CaptureAsync_SingleFile_ProducesOneChunkAndUploadsEncryptedPayload()
    {
        var content = RandomNumberGenerator.GetBytes(512);
        var filePath = Path.Combine(_tempRoot, "data.bin");
        await File.WriteAllBytesAsync(filePath, content);

        var result = await _service.CaptureAsync([_tempRoot], _uploader, TestHelpers.NullReporter<BackupStage>(), CancellationToken.None);

        Assert.That(result.Manifest.Files, Has.Count.EqualTo(1));
        var entry = result.Manifest.Files[0];
        Assert.That(entry.Chunks, Has.Count.EqualTo(1));
        Assert.That(result.NewChunksCount, Is.EqualTo(1));

        var expectedSha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        Assert.That(entry.Chunks[0], Is.EqualTo(expectedSha));

        var objectKey = $"chunks/{expectedSha}";
        Assert.That(_uploader.Uploaded, Contains.Key(objectKey));

        var uploaded = _uploader.Uploaded[objectKey];
        Assert.That(uploaded, Is.Not.EqualTo(content), "uploaded payload must be encrypted, not plaintext");
        Assert.That(DecryptAes(uploaded, _encryptionKey), Is.EqualTo(content),
            "decrypting the uploaded payload must reproduce the original chunk");
    }

    [Test]
    public async Task CaptureAsync_MultiChunkFile_CanBeReassembledFromEncryptedChunks()
    {
        var original = RandomNumberGenerator.GetBytes(ContentDefinedChunker.MaxSize * 2 + 123_456);
        var filePath = Path.Combine(_tempRoot, "large.bin");
        await File.WriteAllBytesAsync(filePath, original);

        var result = await _service.CaptureAsync([_tempRoot], _uploader, TestHelpers.NullReporter<BackupStage>(), CancellationToken.None);

        var entry = result.Manifest.Files.Single();
        Assert.That(entry.Chunks.Count, Is.GreaterThan(1));

        using var reassembled = new MemoryStream();
        foreach (var sha in entry.Chunks)
        {
            var encrypted = _uploader.Uploaded[$"chunks/{sha}"];
            var plaintext = DecryptAes(encrypted, _encryptionKey);
            reassembled.Write(plaintext, 0, plaintext.Length);
        }

        Assert.That(reassembled.ToArray(), Is.EqualTo(original));
    }

    [Test]
    public async Task CaptureAsync_DuplicateChunksWithinFile_UploadedOnlyOnce()
    {
        var zeros = new byte[ContentDefinedChunker.MaxSize * 3];
        await File.WriteAllBytesAsync(Path.Combine(_tempRoot, "zeros.bin"), zeros);

        var result = await _service.CaptureAsync([_tempRoot], _uploader, TestHelpers.NullReporter<BackupStage>(), CancellationToken.None);

        var entry = result.Manifest.Files.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Chunks.Count, Is.GreaterThan(1),
                "file must split into more than one chunk");
            Assert.That(entry.Chunks.Distinct().Count(), Is.LessThan(entry.Chunks.Count),
                "input must produce duplicate chunks — otherwise this test does not exercise within-file dedup");
            Assert.That(result.NewChunksCount, Is.EqualTo(entry.Chunks.Distinct().Count()),
                "new chunks count must equal the number of unique SHAs in the manifest");
            Assert.That(_uploader.Uploaded.Count, Is.EqualTo(result.NewChunksCount),
                "every duplicate chunk must be skipped at upload time");
            Assert.That(_uploader.UploadCalls, Is.EqualTo(result.NewChunksCount),
                "UploadBytesAsync must be called exactly once per unique chunk");
        });
    }

    [Test]
    public async Task CaptureAsync_IdenticalFiles_DedupedViaExistsAsync()
    {
        var content = RandomNumberGenerator.GetBytes(2048);
        await File.WriteAllBytesAsync(Path.Combine(_tempRoot, "first.bin"), content);
        await File.WriteAllBytesAsync(Path.Combine(_tempRoot, "second.bin"), content);

        var result = await _service.CaptureAsync([_tempRoot], _uploader, TestHelpers.NullReporter<BackupStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Manifest.Files, Has.Count.EqualTo(2));
            Assert.That(result.NewChunksCount, Is.EqualTo(1), "second file should reuse the first file's chunk");
            Assert.That(_uploader.UploadCalls, Is.EqualTo(1));
            Assert.That(_uploader.ExistsCalls, Is.EqualTo(2), "ExistsAsync should be consulted once per chunk");
            Assert.That(result.Manifest.Files[0].Chunks[0], Is.EqualTo(result.Manifest.Files[1].Chunks[0]));
        });
    }

    [Test]
    public async Task CaptureAsync_NestedDirectories_UsesForwardSlashRelativePaths()
    {
        var nested = Path.Combine(_tempRoot, "sub", "deep");
        Directory.CreateDirectory(nested);
        await File.WriteAllBytesAsync(Path.Combine(nested, "leaf.bin"), new byte[] { 9 });

        var result = await _service.CaptureAsync([_tempRoot], _uploader, TestHelpers.NullReporter<BackupStage>(), CancellationToken.None);

        Assert.That(result.Manifest.Files, Has.Count.EqualTo(1));
        Assert.That(result.Manifest.Files[0].Path, Is.EqualTo("sub/deep/leaf.bin"));
    }

    [Test]
    public async Task CaptureAsync_FileEntry_PopulatesAllFields()
    {
        var content = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        var filePath = Path.Combine(_tempRoot, "meta.bin");
        await File.WriteAllBytesAsync(filePath, content);
        var expectedMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(filePath)).ToUnixTimeSeconds();

        var result = await _service.CaptureAsync([_tempRoot], _uploader, TestHelpers.NullReporter<BackupStage>(), CancellationToken.None);

        var entry = result.Manifest.Files.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Path, Is.EqualTo("meta.bin"));
            Assert.That(entry.Size, Is.EqualTo(content.Length));
            Assert.That(entry.Mtime, Is.EqualTo(expectedMtime));
            Assert.That(entry.Chunks, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void CaptureAsync_CancellationRequested_Throws()
    {
        for (int i = 0; i < 5; i++)
            File.WriteAllBytes(Path.Combine(_tempRoot, $"f{i}.bin"), new byte[] { (byte)i });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.CaptureAsync([_tempRoot], _uploader, TestHelpers.NullReporter<BackupStage>(), cts.Token));
    }

    private static byte[] DecryptAes(byte[] encrypted, byte[] key) =>
        EncryptionServiceTests.DecryptBytes(encrypted, key);

    private sealed class FakeUploadService : IUploadService
    {
        public Dictionary<string, byte[]> Uploaded { get; } = [];
        public int ExistsCalls { get; private set; }
        public int UploadCalls { get; private set; }

        public Task<string> UploadAsync(string filePath, string folder, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException("FileBackupService must not call UploadAsync");

        public Task UploadBytesAsync(byte[] content, string objectKey, CancellationToken ct)
        {
            UploadCalls++;
            Uploaded[objectKey] = content;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string objectKey, CancellationToken ct)
        {
            ExistsCalls++;
            return Task.FromResult(Uploaded.ContainsKey(objectKey));
        }

        public Task DownloadAsync(string objectKey, string localPath, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException("FileBackupService must not call DownloadAsync");

        public Task<byte[]> DownloadBytesAsync(string objectKey, CancellationToken ct) =>
            throw new NotSupportedException("FileBackupService must not call DownloadBytesAsync");
    }
}
