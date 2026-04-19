using System.Diagnostics;
using System.Security.Cryptography;
using BackupsterAgent.Configuration;
using BackupsterAgent.Enums;
using BackupsterAgent.Providers;
using BackupsterAgent.Services;
using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Upload;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Tests;

[TestFixture]
public sealed class BackupJobTests
{
    private string _tempRoot = null!;
    private FakeUploadService _uploader = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "dbbackup-job-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _uploader = new FakeUploadService();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { }
    }

    [Test]
    public async Task CaptureFilesSafelyAsync_EmptyFilePaths_ReturnsBothNull()
    {
        var job = BuildJob();
        var storage = StorageFor(UploadProvider.S3);
        var config = new DatabaseConfig { Database = "db1", FilePaths = [] };

        var (metrics, error) = await job.CaptureFilesSafelyAsync(
            config, storage, _uploader, backupFolder: "db1/2026-04-17_00-00-00", dumpObjectKey: "db1/.../dump.enc",
            TestHelpers.NullReporter<BackupStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(metrics, Is.Null);
            Assert.That(error, Is.Null);
            Assert.That(_uploader.UploadCalls, Is.Zero, "upload must not be called when FilePaths is empty");
            Assert.That(_uploader.ExistsCalls, Is.Zero);
        });
    }

    [Test]
    public async Task CaptureFilesSafelyAsync_SftpStorage_ReturnsUserFacingError()
    {
        await File.WriteAllBytesAsync(Path.Combine(_tempRoot, "a.bin"), new byte[] { 1, 2, 3 });

        var job = BuildJob();
        var storage = StorageFor(UploadProvider.Sftp);
        var config = new DatabaseConfig { Database = "db1", FilePaths = [_tempRoot] };

        var (metrics, error) = await job.CaptureFilesSafelyAsync(
            config, storage, _uploader, backupFolder: "db1/2026-04-17_00-00-00", dumpObjectKey: "db1/.../dump.enc",
            TestHelpers.NullReporter<BackupStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(metrics, Is.Null);
            Assert.That(error, Is.EqualTo(
                "Бэкап файлов не поддерживается на SFTP-хранилище 'sftp-storage'. Файлы не загружены."));
            Assert.That(_uploader.UploadCalls, Is.Zero, "SFTP branch must short-circuit before touching the uploader");
        });
    }

    [Test]
    public async Task CaptureFilesSafelyAsync_Success_ReturnsMetricsAndNullError()
    {
        var content = RandomNumberGenerator.GetBytes(1024);
        await File.WriteAllBytesAsync(Path.Combine(_tempRoot, "file.bin"), content);

        var job = BuildJob();
        var storage = StorageFor(UploadProvider.S3);
        var config = new DatabaseConfig { Database = "customers", FilePaths = [_tempRoot] };
        const string backupFolder = "customers/2026-04-17_14-30-00";
        const string dumpKey = "customers/2026-04-17_14-30-00/dump.sql.gz.enc";

        var (metrics, error) = await job.CaptureFilesSafelyAsync(
            config, storage, _uploader, backupFolder, dumpKey, TestHelpers.NullReporter<BackupStage>(), CancellationToken.None);

        Assert.That(error, Is.Null);
        Assert.That(metrics, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(metrics!.FilesCount, Is.EqualTo(1));
            Assert.That(metrics.FilesTotalBytes, Is.EqualTo(content.Length));
            Assert.That(metrics.NewChunksCount, Is.EqualTo(1));
            Assert.That(metrics.ManifestKey, Is.EqualTo($"{backupFolder}/manifest.json.enc"));
            Assert.That(_uploader.Uploaded, Contains.Key(metrics.ManifestKey));
        });
    }

    [Test]
    public async Task CaptureFilesSafelyAsync_UploadFails_ReturnsUserFacingErrorWithoutExceptionDetails()
    {
        await File.WriteAllBytesAsync(Path.Combine(_tempRoot, "file.bin"), new byte[] { 1, 2, 3, 4 });
        _uploader.ThrowOnUpload = new InvalidOperationException("simulated S3 403");

        var job = BuildJob();
        var storage = StorageFor(UploadProvider.S3);
        var config = new DatabaseConfig { Database = "db1", FilePaths = [_tempRoot] };

        var (metrics, error) = await job.CaptureFilesSafelyAsync(
            config, storage, _uploader, backupFolder: "db1/2026-04-17_14-30-00", dumpObjectKey: "db1/.../dump.enc",
            TestHelpers.NullReporter<BackupStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(metrics, Is.Null);
            Assert.That(error, Is.EqualTo(
                "Не удалось загрузить файлы в хранилище. Подробности — в логах агента."));
            Assert.That(error, Does.Not.Contain("simulated S3 403"),
                "raw exception messages must not leak to the user-facing field");
        });
    }

    [Test]
    public void CaptureFilesSafelyAsync_Cancelled_ThrowsAndDoesNotProduceError()
    {
        for (int i = 0; i < 3; i++)
            File.WriteAllBytes(Path.Combine(_tempRoot, $"f{i}.bin"), new byte[] { (byte)i });

        var job = BuildJob();
        var storage = StorageFor(UploadProvider.S3);
        var config = new DatabaseConfig { Database = "db1", FilePaths = [_tempRoot] };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() =>
            job.CaptureFilesSafelyAsync(
                config, storage, _uploader, "db1/2026-04-17_14-30-00", "dump.enc",
                TestHelpers.NullReporter<BackupStage>(), cts.Token));
    }

    private static StorageConfig StorageFor(UploadProvider provider) => provider switch
    {
        UploadProvider.S3 => new StorageConfig { Name = "s3-storage", Provider = UploadProvider.S3, S3 = new S3Settings() },
        UploadProvider.Sftp => new StorageConfig { Name = "sftp-storage", Provider = UploadProvider.Sftp, Sftp = new SftpSettings() },
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private BackupJob BuildJob()
    {
        var encKey = RandomNumberGenerator.GetBytes(32);
        var encryption = new EncryptionService(
            Options.Create(new EncryptionSettings { Key = Convert.ToBase64String(encKey) }),
            NullLogger<EncryptionService>.Instance);

        var chunker = new ContentDefinedChunker();
        var fileBackup = new FileBackupService(chunker, encryption, NullLogger<FileBackupService>.Instance);
        var manifestStore = new ManifestStore(encryption, NullLogger<ManifestStore>.Instance);

        return new BackupJob(
            new StubBackupProviderFactory(),
            new ConnectionResolver([]),
            new StorageResolver([]),
            encryption,
            new StubUploadServiceFactory(_uploader),
            fileBackup,
            manifestStore,
            new FakeBackupRecordClient(),
            new FakeProgressReporterFactory(),
            new AgentActivityLock(NullLogger<AgentActivityLock>.Instance),
            Options.Create(new AgentSettings { Token = "test-token", DashboardUrl = "http://localhost" }),
            new ActivitySource("BackupsterAgent.Tests"),
            NullLogger<BackupJob>.Instance);
    }

    private sealed class FakeUploadService : IUploadService
    {
        public Dictionary<string, byte[]> Uploaded { get; } = [];
        public int ExistsCalls { get; private set; }
        public int UploadCalls { get; private set; }
        public Exception? ThrowOnUpload { get; set; }

        public Task<string> UploadAsync(string filePath, string folder, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException("BackupJob file-backup path must not call UploadAsync");

        public Task UploadBytesAsync(byte[] content, string objectKey, CancellationToken ct)
        {
            UploadCalls++;
            if (ThrowOnUpload is not null) throw ThrowOnUpload;
            Uploaded[objectKey] = content;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string objectKey, CancellationToken ct)
        {
            ExistsCalls++;
            return Task.FromResult(Uploaded.ContainsKey(objectKey));
        }

        public Task DownloadAsync(string objectKey, string localPath, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException("BackupJob must not call DownloadAsync");

        public Task<byte[]> DownloadBytesAsync(string objectKey, CancellationToken ct) =>
            throw new NotSupportedException("BackupJob must not call DownloadBytesAsync");

        public IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken ct) =>
            throw new NotSupportedException("BackupJob must not call ListAsync");

        public Task DeleteAsync(string objectKey, CancellationToken ct) =>
            throw new NotSupportedException("BackupJob must not call DeleteAsync");
    }

    private sealed class StubUploadServiceFactory(IUploadService service) : IUploadServiceFactory
    {
        public IUploadService GetService(string storageName) => service;
    }

    private sealed class StubBackupProviderFactory : IBackupProviderFactory
    {
        public IBackupProvider GetProvider(DatabaseType databaseType) =>
            throw new NotSupportedException("CaptureFilesSafelyAsync must not touch the backup provider");
    }
}
