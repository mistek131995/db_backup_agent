using DbBackupAgent.Models;
using Microsoft.Extensions.Options;

namespace DbBackupAgent.Services;

public sealed class UploadServiceFactory : IUploadServiceFactory
{
    private readonly UploadSettings _uploadSettings;
    private readonly S3UploadService _s3;
    private readonly SftpUploadService _sftp;

    public UploadServiceFactory(
        IOptions<UploadSettings> uploadSettings,
        S3UploadService s3,
        SftpUploadService sftp)
    {
        _uploadSettings = uploadSettings.Value;
        _s3 = s3;
        _sftp = sftp;
    }

    public IUploadService GetService() =>
        _uploadSettings.Provider.Equals("Sftp", StringComparison.OrdinalIgnoreCase)
            ? _sftp
            : _s3;
}
