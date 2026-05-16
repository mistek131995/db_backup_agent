namespace BackupsterAgent.Contracts;

public sealed class StorageSyncRequestDto
{
    public List<StorageSyncItemDto> Storages { get; set; } = new();
}
