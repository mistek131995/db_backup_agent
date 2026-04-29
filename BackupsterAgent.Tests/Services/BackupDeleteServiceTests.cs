using BackupsterAgent.Contracts;
using BackupsterAgent.Enums;
using BackupsterAgent.Providers.Upload;
using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common.Progress;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupsterAgent.Tests.Services;

[TestFixture]
public sealed class BackupDeleteServiceTests
{
    private const string StorageName = "s3-main";
    private FakeDeleteTrackingProvider _uploader = null!;
    private BackupDeleteService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _uploader = new FakeDeleteTrackingProvider();
        _service = new BackupDeleteService(
            new StubUploadFactory(StorageName, _uploader),
            NullLogger<BackupDeleteService>.Instance);
    }

    [Test]
    public async Task RunAsync_StorageNotFound_ReturnsFailedWithRuMessage()
    {
        var service = new BackupDeleteService(
            new ThrowingUploadFactory(),
            NullLogger<BackupDeleteService>.Instance);

        var payload = new DeleteTaskPayload { StorageName = "missing-storage", DumpObjectKey = "key" };
        var result = await service.RunAsync(Guid.NewGuid(), payload, reporter: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("missing-storage"));
            Assert.That(result.ErrorMessage, Does.Contain("Хранилище"));
        });
    }

    [Test]
    public async Task RunAsync_BothKeys_DeletesManifestBeforeDump()
    {
        var payload = new DeleteTaskPayload
        {
            StorageName = StorageName,
            ManifestKey = "folder/manifest.json.gz.enc",
            DumpObjectKey = "folder/dump.sql.gz.enc",
        };

        var result = await _service.RunAsync(Guid.NewGuid(), payload, reporter: null, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(_uploader.DeleteOrder, Is.EqualTo(new[]
        {
            "folder/manifest.json.gz.enc",
            "folder/dump.sql.gz.enc",
        }));
    }

    [Test]
    public async Task RunAsync_OnlyDumpKey_DeletesOnlyDump()
    {
        var payload = new DeleteTaskPayload
        {
            StorageName = StorageName,
            DumpObjectKey = "folder/dump.sql.gz.enc",
            ManifestKey = null,
        };

        var result = await _service.RunAsync(Guid.NewGuid(), payload, reporter: null, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(_uploader.DeleteOrder, Is.EqualTo(new[] { "folder/dump.sql.gz.enc" }));
    }

    [Test]
    public async Task RunAsync_OnlyManifestKey_DeletesOnlyManifest()
    {
        var payload = new DeleteTaskPayload
        {
            StorageName = StorageName,
            ManifestKey = "folder/manifest.json.gz.enc",
            DumpObjectKey = null,
        };

        var result = await _service.RunAsync(Guid.NewGuid(), payload, reporter: null, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(_uploader.DeleteOrder, Is.EqualTo(new[] { "folder/manifest.json.gz.enc" }));
    }

    [Test]
    public async Task RunAsync_ManifestDeleteFails_ReturnsFailed_DoesNotDeleteDump()
    {
        _uploader.ThrowOnDelete["folder/manifest.json.gz.enc"] = new IOException("S3 error");

        var payload = new DeleteTaskPayload
        {
            StorageName = StorageName,
            ManifestKey = "folder/manifest.json.gz.enc",
            DumpObjectKey = "folder/dump.sql.gz.enc",
        };

        var result = await _service.RunAsync(Guid.NewGuid(), payload, reporter: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("манифест"));
        });
        Assert.That(_uploader.DeleteOrder, Is.Empty, "dump must not be deleted if manifest deletion failed");
    }

    [Test]
    public async Task RunAsync_DumpDeleteFails_ReturnsFailed()
    {
        _uploader.ThrowOnDelete["folder/dump.sql.gz.enc"] = new IOException("S3 error");

        var payload = new DeleteTaskPayload
        {
            StorageName = StorageName,
            DumpObjectKey = "folder/dump.sql.gz.enc",
        };

        var result = await _service.RunAsync(Guid.NewGuid(), payload, reporter: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("дамп"));
        });
    }

    [Test]
    public void RunAsync_Cancellation_Propagates()
    {
        var payload = new DeleteTaskPayload
        {
            StorageName = StorageName,
            ManifestKey = "key/manifest.json.gz.enc",
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.RunAsync(Guid.NewGuid(), payload, reporter: null, cts.Token));
    }

    [Test]
    public async Task RunAsync_ReportsProgressStages()
    {
        var payload = new DeleteTaskPayload
        {
            StorageName = StorageName,
            ManifestKey = "folder/manifest.json.gz.enc",
            DumpObjectKey = "folder/dump.sql.gz.enc",
        };
        var reporter = new RecordingProgressReporter<DeleteStage>();

        await _service.RunAsync(Guid.NewGuid(), payload, reporter, CancellationToken.None);

        Assert.That(reporter.Stages, Is.EqualTo(new[]
        {
            DeleteStage.Resolving,
            DeleteStage.DeletingManifest,
            DeleteStage.DeletingDump,
            DeleteStage.Completed,
        }));
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

    private sealed class ThrowingUploadFactory : IUploadProviderFactory
    {
        public IUploadProvider GetProvider(string name) =>
            throw new InvalidOperationException($"Storage '{name}' not found in config.");
    }

    private sealed class RecordingProgressReporter<T> : IProgressReporter<T> where T : struct, Enum
    {
        public List<T> Stages { get; } = [];

        public void Report(T stage, long? processed = null, long? total = null, string? unit = null, string? currentItem = null)
            => Stages.Add(stage);

        public ValueTask DisposeAsync() => default;
    }
}
