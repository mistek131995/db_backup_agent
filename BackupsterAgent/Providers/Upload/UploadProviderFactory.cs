using System.Collections.Concurrent;
using BackupsterAgent.Configuration;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Common.Resolvers;

namespace BackupsterAgent.Providers.Upload;

public sealed class UploadProviderFactory : IUploadProviderFactory, IAsyncDisposable
{
    private readonly StorageResolver _storages;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, IUploadProvider> _cache = new(StringComparer.Ordinal);
    private bool _disposed;

    public UploadProviderFactory(StorageResolver storages, ILoggerFactory loggerFactory)
    {
        _storages = storages;
        _loggerFactory = loggerFactory;
    }

    public IUploadProvider GetProvider(string storageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _cache.GetOrAdd(storageName, Create);
    }

    private IUploadProvider Create(string storageName)
    {
        var storage = _storages.Resolve(storageName);

        return storage.Provider switch
        {
            UploadProvider.S3 => new S3UploadProvider(
                storage.S3 ?? throw new InvalidOperationException(
                    $"Storage '{storageName}' has Provider=S3 but S3 settings are missing."),
                _loggerFactory.CreateLogger<S3UploadProvider>()),
            UploadProvider.Sftp => new SftpUploadProvider(
                storage.Sftp ?? throw new InvalidOperationException(
                    $"Storage '{storageName}' has Provider=Sftp but Sftp settings are missing."),
                _loggerFactory.CreateLogger<SftpUploadProvider>()),
            UploadProvider.AzureBlob => new AzureBlobUploadProvider(
                storage.AzureBlob ?? throw new InvalidOperationException(
                    $"Storage '{storageName}' has Provider=AzureBlob but AzureBlob settings are missing."),
                _loggerFactory.CreateLogger<AzureBlobUploadProvider>()),
            UploadProvider.WebDav => new WebDavUploadProvider(
                storage.WebDav ?? throw new InvalidOperationException(
                    $"Storage '{storageName}' has Provider=WebDav but WebDav settings are missing."),
                _loggerFactory.CreateLogger<WebDavUploadProvider>()),
            UploadProvider.LocalFs => new LocalFsUploadProvider(
                storage.LocalFs ?? throw new InvalidOperationException(
                    $"Storage '{storageName}' has Provider=LocalFs but LocalFs settings are missing."),
                _loggerFactory.CreateLogger<LocalFsUploadProvider>()),
            _ => throw new InvalidOperationException(
                $"Storage '{storageName}' has unknown provider: {storage.Provider}"),
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var provider in _cache.Values)
        {
            switch (provider)
            {
                case IAsyncDisposable asyncDisposable:
                    try { await asyncDisposable.DisposeAsync(); }
                    catch { /* swallow — best-effort shutdown */ }
                    break;
                case IDisposable disposable:
                    try { disposable.Dispose(); }
                    catch { /* swallow — best-effort shutdown */ }
                    break;
            }
        }

        _cache.Clear();
    }
}
