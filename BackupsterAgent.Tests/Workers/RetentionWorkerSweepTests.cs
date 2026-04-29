using System.Reflection;
using BackupsterAgent.Configuration;
using BackupsterAgent.Contracts;
using BackupsterAgent.Enums;
using BackupsterAgent.Providers.Upload;
using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Common.Resolvers;
using BackupsterAgent.Services.Dashboard.Clients;
using BackupsterAgent.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Tests.Workers;

[TestFixture]
public sealed class RetentionWorkerSweepTests
{
    private const string StorageName = "s3-main";

    private FakeRetentionClient _client = null!;
    private FakeDeleteTrackingProvider _uploader = null!;
    private BackupDeleteService _deleter = null!;
    private StorageResolver _storages = null!;

    [SetUp]
    public void SetUp()
    {
        _client = new FakeRetentionClient();
        _uploader = new FakeDeleteTrackingProvider();
        _deleter = new BackupDeleteService(
            new StubUploadFactory(StorageName, _uploader),
            NullLogger<BackupDeleteService>.Instance);
        _storages = new StorageResolver([
            new StorageConfig { Name = StorageName, Provider = UploadProvider.S3, S3 = new S3Settings() },
        ]);
    }

    private async Task RunSweepAsync(int batchSize = 100, CancellationToken ct = default)
    {
        var worker = new RetentionWorker(
            _client,
            _storages,
            _deleter,
            Options.Create(new RetentionSettings { Enabled = true, IntervalHours = 6, BatchSize = batchSize }),
            new NoOpActivityLock(),
            NullLogger<RetentionWorker>.Instance);

        var method = typeof(RetentionWorker).GetMethod(
            "SweepAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)method.Invoke(worker, [batchSize, ct])!;
    }

    [Test]
    public async Task SweepAsync_EmptyBatch_DoesNotCallDelete()
    {
        _client.ExpiredRecords = [];

        await RunSweepAsync();

        Assert.That(_uploader.DeleteOrder, Is.Empty);
        Assert.That(_client.DeletedIds, Is.Empty);
        Assert.That(_client.UnreachableIds, Is.Empty);
    }

    [Test]
    public async Task SweepAsync_UnknownStorage_MarksAsUnreachable_SkipsStorageDelete()
    {
        var id = Guid.NewGuid();
        _client.ExpiredRecords = [
            new ExpiredBackupRecordDto { Id = id, StorageName = "unknown-storage", DumpObjectKey = "dump.enc" }
        ];

        await RunSweepAsync();

        Assert.That(_uploader.DeleteOrder, Is.Empty);
        Assert.That(_client.DeletedIds, Is.Empty);
        Assert.That(_client.UnreachableIds, Does.Contain(id));
    }

    [Test]
    public async Task SweepAsync_NullOrEmptyStorageName_MarksAsUnreachable()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        _client.ExpiredRecords = [
            new ExpiredBackupRecordDto { Id = id1, StorageName = "", DumpObjectKey = "dump.enc" },
            new ExpiredBackupRecordDto { Id = id2, StorageName = "   ", DumpObjectKey = "dump.enc" },
        ];

        await RunSweepAsync();

        Assert.That(_client.UnreachableIds, Is.EquivalentTo(new[] { id1, id2 }));
        Assert.That(_client.DeletedIds, Is.Empty);
    }

    [Test]
    public async Task SweepAsync_KnownStorage_DeletesStorageObjectsAndCallsDashboard()
    {
        var id = Guid.NewGuid();
        _client.ExpiredRecords = [
            new ExpiredBackupRecordDto
            {
                Id = id,
                StorageName = StorageName,
                ManifestKey = "folder/manifest.json.gz.enc",
                DumpObjectKey = "folder/dump.sql.gz.enc",
            }
        ];

        await RunSweepAsync();

        Assert.That(_uploader.DeleteOrder, Is.EqualTo(new[]
        {
            "folder/manifest.json.gz.enc",
            "folder/dump.sql.gz.enc",
        }));
        Assert.That(_client.DeletedIds, Does.Contain(id));
        Assert.That(_client.UnreachableIds, Is.Empty);
    }

    [Test]
    public async Task SweepAsync_StorageDeleteFails_DoesNotCallDashboardDelete()
    {
        var id = Guid.NewGuid();
        _uploader.ThrowOnDelete["folder/dump.sql.gz.enc"] = new IOException("S3 error");
        _client.ExpiredRecords = [
            new ExpiredBackupRecordDto
            {
                Id = id,
                StorageName = StorageName,
                DumpObjectKey = "folder/dump.sql.gz.enc",
            }
        ];

        await RunSweepAsync();

        Assert.That(_client.DeletedIds, Is.Empty);
        Assert.That(_client.UnreachableIds, Is.Empty);
    }

    [Test]
    public async Task SweepAsync_DashboardDeleteFails_StorageAlreadyCleaned()
    {
        var id = Guid.NewGuid();
        _client.ThrowOnDelete = new HttpRequestException("dashboard unavailable");
        _client.ExpiredRecords = [
            new ExpiredBackupRecordDto
            {
                Id = id,
                StorageName = StorageName,
                DumpObjectKey = "folder/dump.sql.gz.enc",
            }
        ];

        await RunSweepAsync();

        Assert.That(_uploader.DeleteOrder, Does.Contain("folder/dump.sql.gz.enc"));
        Assert.That(_client.UnreachableIds, Is.Empty);
    }

    [Test]
    public async Task SweepAsync_Mixed_HandlesEachRecordIndependently()
    {
        var unreachableId = Guid.NewGuid();
        var successId = Guid.NewGuid();
        var storageFailId = Guid.NewGuid();

        _uploader.ThrowOnDelete["fail/dump.enc"] = new IOException("S3 error");

        _client.ExpiredRecords = [
            new ExpiredBackupRecordDto { Id = unreachableId, StorageName = "gone-storage", DumpObjectKey = "gone/dump.enc" },
            new ExpiredBackupRecordDto { Id = successId, StorageName = StorageName, DumpObjectKey = "ok/dump.enc" },
            new ExpiredBackupRecordDto { Id = storageFailId, StorageName = StorageName, DumpObjectKey = "fail/dump.enc" },
        ];

        await RunSweepAsync();

        Assert.That(_client.UnreachableIds, Does.Contain(unreachableId));
        Assert.That(_client.DeletedIds, Does.Contain(successId));
        Assert.That(_client.DeletedIds, Does.Not.Contain(storageFailId));
        Assert.That(_client.DeletedIds, Does.Not.Contain(unreachableId));
    }

    private sealed class FakeRetentionClient : IRetentionClient
    {
        public List<ExpiredBackupRecordDto> ExpiredRecords { get; set; } = [];
        public List<Guid> DeletedIds { get; } = [];
        public List<Guid> UnreachableIds { get; } = [];
        public Exception? ThrowOnDelete { get; set; }

        public Task<IReadOnlyList<ExpiredBackupRecordDto>> GetExpiredAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExpiredBackupRecordDto>>(ExpiredRecords);

        public Task DeleteAsync(Guid recordId, CancellationToken ct)
        {
            if (ThrowOnDelete is not null) throw ThrowOnDelete;
            DeletedIds.Add(recordId);
            return Task.CompletedTask;
        }

        public Task MarkStorageUnreachableAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
        {
            UnreachableIds.AddRange(ids);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDeleteTrackingProvider : IUploadProvider
    {
        public List<string> DeleteOrder { get; } = [];
        public Dictionary<string, Exception> ThrowOnDelete { get; } = [];

        public Task DeleteAsync(string objectKey, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (ThrowOnDelete.TryGetValue(objectKey, out var ex)) throw ex;
            DeleteOrder.Add(objectKey);
            return Task.CompletedTask;
        }

        public Task<string> UploadAsync(string filePath, string folder, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task UploadBytesAsync(byte[] content, string objectKey, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> ExistsAsync(string objectKey, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<long> GetObjectSizeAsync(string objectKey, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DownloadAsync(string objectKey, string localPath, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<byte[]> DownloadBytesAsync(string objectKey, CancellationToken ct) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StubUploadFactory(string storageName, IUploadProvider provider) : IUploadProviderFactory
    {
        public IUploadProvider GetProvider(string name) =>
            name == storageName
                ? provider
                : throw new InvalidOperationException($"Storage '{name}' not found.");
    }

    private sealed class NoOpActivityLock : IAgentActivityLock
    {
        public Task<IDisposable> AcquireAsync(string activityName, CancellationToken ct) =>
            Task.FromResult<IDisposable>(new NoOpDisposable());

        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
