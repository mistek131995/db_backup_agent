using System.Diagnostics;
using System.Security.Cryptography;
using BackupsterAgent.Configuration;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Providers;
using BackupsterAgent.Services;
using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Dashboard;
using BackupsterAgent.Services.Upload;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Tests;

[TestFixture]
public sealed class BackupJobRunTests
{
    private string _tempRoot = null!;
    private string _outboxRoot = null!;
    private FakeRecordingUploadService _uploader = null!;
    private FakeBackupRecordClient _recordClient = null!;
    private OutboxStore _outboxStore = null!;
    private StubBackupProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "dbbackup-runtests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);

        _outboxRoot = Path.Combine(_tempRoot, "outbox");
        _outboxStore = new OutboxStore(_outboxRoot, NullLogger<OutboxStore>.Instance);

        _uploader = new FakeRecordingUploadService();
        _recordClient = new FakeBackupRecordClient();
        _provider = new StubBackupProvider(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { }
    }

    [Test]
    public async Task RunAsync_OnlineHappyPath_FinalizesAndDoesNotEnqueue()
    {
        var serverId = Guid.NewGuid();
        _recordClient.NextOpen = new OpenRecordResult(DashboardAvailability.Ok, serverId);
        _recordClient.NextFinalize = new FinalizeRecordResult(DashboardAvailability.Ok);

        var result = await BuildJob().RunAsync(Config(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_recordClient.OpenCalls, Is.EqualTo(1));
            Assert.That(_recordClient.FinalizeCalls, Is.EqualTo(1));
            Assert.That(_recordClient.LastFinalize!.Status, Is.EqualTo(BackupStatus.Success));
            Assert.That(_recordClient.LastOpen!.StartedAt, Is.Not.Null);
        });

        var outbox = await _outboxStore.ListAsync(CancellationToken.None);
        Assert.That(outbox, Is.Empty, "online happy path must not touch outbox");
    }

    [Test]
    public async Task RunAsync_OfflineAtStart_EnqueuesEntryWithoutServerId_DoesNotFinalize()
    {
        _recordClient.NextOpen = new OpenRecordResult(DashboardAvailability.OfflineRetryable);

        var result = await BuildJob().RunAsync(Config(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_recordClient.OpenCalls, Is.EqualTo(1));
            Assert.That(_recordClient.FinalizeCalls, Is.Zero, "offline path must not call FinalizeAsync");
        });

        var outbox = await _outboxStore.ListAsync(CancellationToken.None);
        Assert.That(outbox, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(outbox[0].ServerRecordId, Is.Null);
            Assert.That(outbox[0].Status, Is.EqualTo("success"));
            Assert.That(outbox[0].DatabaseName, Is.EqualTo("db1"));
            Assert.That(outbox[0].StorageName, Is.EqualTo("s3-main"));
            Assert.That(outbox[0].DumpObjectKey, Is.Not.Null);
        });
    }

    [Test]
    public async Task RunAsync_OfflineAtFinalize_EnqueuesEntryWithServerId()
    {
        var serverId = Guid.NewGuid();
        _recordClient.NextOpen = new OpenRecordResult(DashboardAvailability.Ok, serverId);
        _recordClient.NextFinalize = new FinalizeRecordResult(DashboardAvailability.OfflineRetryable);

        var result = await BuildJob().RunAsync(Config(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_recordClient.OpenCalls, Is.EqualTo(1));
            Assert.That(_recordClient.FinalizeCalls, Is.EqualTo(1));
        });

        var outbox = await _outboxStore.ListAsync(CancellationToken.None);
        Assert.That(outbox, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(outbox[0].ServerRecordId, Is.EqualTo(serverId));
            Assert.That(outbox[0].Status, Is.EqualTo("success"));
        });
    }

    [Test]
    public async Task RunAsync_PermanentSkipOnOpen_ReturnsFailedAndDoesNotEnqueue()
    {
        _recordClient.NextOpen = new OpenRecordResult(DashboardAvailability.PermanentSkip);

        var result = await BuildJob().RunAsync(Config(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("skipped"));
            Assert.That(_recordClient.OpenCalls, Is.EqualTo(1));
            Assert.That(_recordClient.FinalizeCalls, Is.Zero);
            Assert.That(_provider.BackupCalls, Is.Zero, "permanent skip must not run the pipeline");
        });

        var outbox = await _outboxStore.ListAsync(CancellationToken.None);
        Assert.That(outbox, Is.Empty);
    }

    private static DatabaseConfig Config() => new()
    {
        ConnectionName = "pg-main",
        StorageName = "s3-main",
        Database = "db1",
        FilePaths = [],
    };

    private BackupJob BuildJob()
    {
        var encKey = RandomNumberGenerator.GetBytes(32);
        var encryption = new EncryptionService(
            Options.Create(new EncryptionSettings { Key = Convert.ToBase64String(encKey) }),
            NullLogger<EncryptionService>.Instance);

        var chunker = new ContentDefinedChunker();
        var fileBackup = new FileBackupService(chunker, encryption, NullLogger<FileBackupService>.Instance);
        var manifestStore = new ManifestStore(
            encryption,
            Options.Create(new RestoreSettings { TempPath = Path.Combine(_tempRoot, "manifest-temp") }),
            NullLoggerFactory.Instance,
            NullLogger<ManifestStore>.Instance);

        var connections = new ConnectionResolver([
            new ConnectionConfig { Name = "pg-main", DatabaseType = DatabaseType.Postgres, Host = "localhost", Port = 5432 },
        ]);
        var storages = new StorageResolver([
            new StorageConfig { Name = "s3-main", Provider = UploadProvider.S3, S3 = new S3Settings() },
        ]);

        return new BackupJob(
            new StubProviderFactory(_provider),
            connections,
            storages,
            encryption,
            new StubUploadFactory(_uploader),
            fileBackup,
            manifestStore,
            _recordClient,
            new FakeProgressReporterFactory(),
            _outboxStore,
            Options.Create(new AgentSettings { Token = "test-token", DashboardUrl = "http://localhost" }),
            new ActivitySource("BackupsterAgent.Tests"),
            NullLogger<BackupJob>.Instance);
    }

    private sealed class StubBackupProvider(string tempRoot) : IBackupProvider
    {
        public int BackupCalls { get; private set; }

        public async Task<BackupResult> BackupAsync(DatabaseConfig config, ConnectionConfig connection, CancellationToken ct)
        {
            BackupCalls++;
            var path = Path.Combine(tempRoot, $"dump-{Guid.NewGuid():N}.sql.gz");
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4, 5 }, ct);
            return new BackupResult
            {
                FilePath = path,
                SizeBytes = 5,
                DurationMs = 100,
                Success = true,
            };
        }
    }

    private sealed class StubProviderFactory(IBackupProvider provider) : IBackupProviderFactory
    {
        public IBackupProvider GetProvider(DatabaseType databaseType) => provider;
    }

    private sealed class StubUploadFactory(IUploadService service) : IUploadServiceFactory
    {
        public IUploadService GetService(string storageName) => service;
    }

    private sealed class FakeRecordingUploadService : IUploadService
    {
        public Task<string> UploadAsync(string filePath, string folder, IProgress<long>? progress, CancellationToken ct) =>
            Task.FromResult($"fake://{folder}/{Path.GetFileName(filePath)}");

        public Task UploadBytesAsync(byte[] content, string objectKey, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> ExistsAsync(string objectKey, CancellationToken ct) => Task.FromResult(false);

        public Task DownloadAsync(string objectKey, string localPath, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<byte[]> DownloadBytesAsync(string objectKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string objectKey, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
