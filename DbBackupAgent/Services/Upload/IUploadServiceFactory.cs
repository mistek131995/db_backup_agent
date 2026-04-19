namespace DbBackupAgent.Services.Upload;

public interface IUploadServiceFactory
{
    IUploadService GetService(string storageName);
}
