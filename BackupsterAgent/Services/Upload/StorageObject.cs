namespace BackupsterAgent.Services.Upload;

public sealed record StorageObject(string Key, DateTime LastModifiedUtc, long Size);
