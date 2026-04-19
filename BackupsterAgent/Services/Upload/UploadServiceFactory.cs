using System.Collections.Concurrent;
using BackupsterAgent.Configuration;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Common;

namespace BackupsterAgent.Services.Upload;

public sealed class UploadServiceFactory : IUploadServiceFactory
{
    private readonly StorageResolver _storages;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, IUploadService> _cache = new(StringComparer.Ordinal);

    public UploadServiceFactory(StorageResolver storages, ILoggerFactory loggerFactory)
    {
        _storages = storages;
        _loggerFactory = loggerFactory;
    }

    public IUploadService GetService(string storageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageName);

        return _cache.GetOrAdd(storageName, Create);
    }

    private IUploadService Create(string storageName)
    {
        var storage = _storages.Resolve(storageName);

        return storage.Provider switch
        {
            UploadProvider.S3 => new S3UploadService(
                storage.S3 ?? throw new InvalidOperationException(
                    $"Storage '{storageName}' has Provider=S3 but S3 settings are missing."),
                _loggerFactory.CreateLogger<S3UploadService>()),
            UploadProvider.Sftp => new SftpUploadService(
                storage.Sftp ?? throw new InvalidOperationException(
                    $"Storage '{storageName}' has Provider=Sftp but Sftp settings are missing."),
                _loggerFactory.CreateLogger<SftpUploadService>()),
            _ => throw new InvalidOperationException(
                $"Storage '{storageName}' has unknown provider: {storage.Provider}"),
        };
    }
}
