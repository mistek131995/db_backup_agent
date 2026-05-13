using System.Security.Cryptography;
using BackupsterAgent.Configuration;
using BackupsterAgent.Enums;
using BackupsterAgent.Providers.Upload;
using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common.Progress;
using BackupsterAgent.Services.Common.Security;
using BackupsterAgent.Services.Restore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.IntegrationTests.Backup;

[TestFixture]
[Category("Integration")]
public sealed class SftpBackupIntegrationTests
{
    private const string ManifestSuffix = "/manifest.json.gz.enc";
    private const string ChunksPrefix = "chunks/";

    private SftpSettings _baseSettings = null!;
    private string _sourcePath = null!;
    private byte[] _encryptionKey = null!;

    private SftpUploadProvider _provider = null!;
    private string _testTempRoot = null!;
    private EncryptionService _encryption = null!;
    private ManifestStore _manifestStore = null!;
    private FileBackupService _fileBackup = null!;
    private FileRestoreService _fileRestore = null!;
    private CancellationTokenSource _cts = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Assume.That(
            IntegrationConfig.TryGetSftpSettings(out var settings),
            Is.True,
            "Sftp:* not configured; set via dotnet user-secrets or BACKUPSTER_INTEGRATION_SFTP__* env vars.");
        Assume.That(
            IntegrationConfig.TryGetBackupSourcePath(out var src),
            Is.True,
            "Backup:SourcePath not configured or directory not found; set via dotnet user-secrets or BACKUPSTER_INTEGRATION_BACKUP__SOURCEPATH env var.");

        _baseSettings = settings;
        _sourcePath = src;
        _encryptionKey = RandomNumberGenerator.GetBytes(32);
    }

    [SetUp]
    public void SetUp()
    {
        var prefix = IntegrationConfig.MakeUniquePrefix(TestContext.CurrentContext.Test.MethodName ?? "test");
        var settings = new SftpSettings
        {
            Host = _baseSettings.Host,
            Port = _baseSettings.Port,
            Username = _baseSettings.Username,
            Password = _baseSettings.Password,
            PrivateKeyPath = _baseSettings.PrivateKeyPath,
            PrivateKeyPassphrase = _baseSettings.PrivateKeyPassphrase,
            HostKeyFingerprint = _baseSettings.HostKeyFingerprint,
            RemotePath = CombinePath(_baseSettings.RemotePath, prefix),
        };
        _provider = new SftpUploadProvider(settings, NullLogger<SftpUploadProvider>.Instance);

        _testTempRoot = Path.Combine(Path.GetTempPath(), $"backupster-sftp-itest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempRoot);

        _encryption = new EncryptionService(
            Options.Create(new EncryptionSettings { Key = Convert.ToBase64String(_encryptionKey) }),
            NullLogger<EncryptionService>.Instance);

        var restoreSettings = Options.Create(new RestoreSettings { TempPath = _testTempRoot });

        _manifestStore = new ManifestStore(
            _encryption, restoreSettings, NullLoggerFactory.Instance,
            NullLogger<ManifestStore>.Instance);

        _fileBackup = new FileBackupService(
            new ContentDefinedChunker(), _encryption,
            NullLogger<FileBackupService>.Instance);

        _fileRestore = new FileRestoreService(
            _encryption, _manifestStore, restoreSettings,
            NullLogger<FileRestoreService>.Instance);

        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    }

    [TearDown]
    public async Task TearDown()
    {
        try
        {
            if (_provider is not null)
                await IntegrationConfig.CleanupPrefixAsync(_provider, string.Empty, CancellationToken.None);
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"SFTP cleanup failed: {ex.Message}");
        }

        if (_provider is not null)
            await _provider.DisposeAsync();
        _cts?.Dispose();

        try
        {
            if (Directory.Exists(_testTempRoot))
                Directory.Delete(_testTempRoot, recursive: true);
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"Local temp cleanup failed: {ex.Message}");
        }
    }

    [Test]
    public async Task CaptureAsync_RealFolder_CreatesManifestAndChunks()
    {
        var reporter = new NullProgressReporter<BackupStage>();

        string manifestKey;
        long writerFilesCount;
        int newChunks;
        await using (var writer = _manifestStore.OpenWriter("itest-db", dumpObjectKey: string.Empty))
        {
            var result = await _fileBackup.CaptureAsync(
                [_sourcePath], _provider, writer, reporter, _cts.Token);
            newChunks = result.NewChunksCount;
            writerFilesCount = writer.FilesCount;
            manifestKey = await writer.CompleteAsync(_provider, "capture-single", _cts.Token);
        }

        var expectedFiles = CountSourceFiles();
        var manifestExists = await _provider.ExistsAsync(manifestKey, _cts.Token);
        var chunkCount = await CountChunksAsync();

        Assert.Multiple(() =>
        {
            Assert.That(manifestExists, Is.True);
            Assert.That(writerFilesCount, Is.EqualTo(expectedFiles));
            Assert.That(newChunks, Is.GreaterThan(0), "fresh sandbox should upload at least one chunk");
            Assert.That(chunkCount, Is.GreaterThan(0));
        });
    }

    [Test]
    public async Task CaptureAsync_SecondRunOnSameSource_HasZeroNewChunks()
    {
        var reporter = new NullProgressReporter<BackupStage>();

        await using (var writer1 = _manifestStore.OpenWriter("itest-db", dumpObjectKey: string.Empty))
        {
            await _fileBackup.CaptureAsync([_sourcePath], _provider, writer1, reporter, _cts.Token);
            await writer1.CompleteAsync(_provider, "dedup-run-1", _cts.Token);
        }

        int newChunksSecondRun;
        await using (var writer2 = _manifestStore.OpenWriter("itest-db", dumpObjectKey: string.Empty))
        {
            var result = await _fileBackup.CaptureAsync(
                [_sourcePath], _provider, writer2, reporter, _cts.Token);
            newChunksSecondRun = result.NewChunksCount;
            await writer2.CompleteAsync(_provider, "dedup-run-2", _cts.Token);
        }

        Assert.That(newChunksSecondRun, Is.Zero);
    }

    [Test]
    public async Task RestoreFiles_RoundTrip_PreservesContent()
    {
        var backupReporter = new NullProgressReporter<BackupStage>();
        var restoreReporter = new NullProgressReporter<RestoreStage>();

        string manifestKey;
        await using (var writer = _manifestStore.OpenWriter("itest-db", dumpObjectKey: string.Empty))
        {
            await _fileBackup.CaptureAsync([_sourcePath], _provider, writer, backupReporter, _cts.Token);
            manifestKey = await writer.CompleteAsync(_provider, "round-trip", _cts.Token);
        }

        var restoreTarget = Path.Combine(_testTempRoot, $"restore-{Guid.NewGuid():N}");
        var restoreResult = await _fileRestore.RunAsync(
            manifestKey, restoreTarget, _provider, restoreReporter, _cts.Token);

        Assert.That(restoreResult.Status, Is.EqualTo(RestoreFilesStatus.Success), restoreResult.ErrorMessage);

        var sourceMap = BuildRelativeMap(_sourcePath);
        var targetMap = BuildRelativeMap(restoreTarget);

        Assert.That(targetMap.Keys, Is.EquivalentTo(sourceMap.Keys));

        int mismatches = 0;
        foreach (var (rel, sourceFull) in sourceMap)
        {
            var sourceHash = await HashFileAsync(sourceFull, _cts.Token);
            var targetHash = await HashFileAsync(targetMap[rel], _cts.Token);
            if (!sourceHash.SequenceEqual(targetHash))
                mismatches++;
        }

        Assert.That(mismatches, Is.Zero, $"{mismatches} файл(ов) восстановились с другим содержимым");
    }

    [Test]
    public async Task ChunkGc_AfterManifestDeletion_RemovesOrphanedChunks()
    {
        var reporter = new NullProgressReporter<BackupStage>();

        string manifestKey;
        await using (var writer = _manifestStore.OpenWriter("itest-db", dumpObjectKey: string.Empty))
        {
            await _fileBackup.CaptureAsync([_sourcePath], _provider, writer, reporter, _cts.Token);
            manifestKey = await writer.CompleteAsync(_provider, "gc-orphan", _cts.Token);
        }

        var chunksBefore = await CountChunksAsync();
        Assume.That(chunksBefore, Is.GreaterThan(0));

        await _provider.DeleteAsync(manifestKey, _cts.Token);

        var (deleted, kept) = await RunSweepAsync(graceWindow: TimeSpan.Zero, _cts.Token);

        Assert.Multiple(() =>
        {
            Assert.That(deleted, Is.EqualTo(chunksBefore), "все осиротевшие чанки должны исчезнуть");
            Assert.That(kept, Is.Zero, "ничего активного не остаётся, поскольку единственный манифест удалён");
        });
    }

    private async Task<(int Deleted, int Kept)> RunSweepAsync(TimeSpan graceWindow, CancellationToken ct)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var obj in _provider.ListAsync(string.Empty, ct))
        {
            if (!obj.Key.EndsWith(ManifestSuffix, StringComparison.Ordinal)) continue;
            await using var reader = await _manifestStore.OpenReaderAsync(obj.Key, _provider, ct);
            await foreach (var entry in reader.ReadFilesAsync(ct))
                foreach (var c in entry.Chunks)
                    referenced.Add(c);
        }

        var cutoff = DateTime.UtcNow - graceWindow;
        int deleted = 0;
        int kept = 0;

        await foreach (var obj in _provider.ListAsync(ChunksPrefix, ct))
        {
            if (!obj.Key.StartsWith(ChunksPrefix, StringComparison.Ordinal)) continue;
            var sha = obj.Key[ChunksPrefix.Length..];
            if (sha.Length == 0) continue;

            if (referenced.Contains(sha))
            {
                kept++;
                continue;
            }

            if (obj.LastModifiedUtc > cutoff) continue;

            await _provider.DeleteAsync(obj.Key, ct);
            deleted++;
        }

        return (deleted, kept);
    }

    private async Task<int> CountChunksAsync()
    {
        int n = 0;
        await foreach (var _ in _provider.ListAsync(ChunksPrefix, _cts.Token)) n++;
        return n;
    }

    private int CountSourceFiles() =>
        Directory.EnumerateFiles(_sourcePath, "*", new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        }).Count();

    private static Dictionary<string, string> BuildRelativeMap(string root) =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            })
            .ToDictionary(
                full => Path.GetRelativePath(root, full).Replace('\\', '/'),
                full => full,
                StringComparer.Ordinal);

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return await sha.ComputeHashAsync(stream, ct);
    }

    private static string CombinePath(string left, string right)
    {
        var l = left.TrimEnd('/');
        var r = right.TrimStart('/');
        return string.IsNullOrEmpty(l) ? "/" + r : $"{l}/{r}";
    }
}
