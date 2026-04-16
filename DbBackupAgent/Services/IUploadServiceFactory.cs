namespace DbBackupAgent.Services;

public interface IUploadServiceFactory
{
    IUploadService GetService();
}
