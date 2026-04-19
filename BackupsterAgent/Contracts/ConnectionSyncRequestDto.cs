namespace BackupsterAgent.Contracts;

public sealed class ConnectionSyncRequestDto
{
    public List<ConnectionSyncItemDto> Connections { get; set; } = new();
}
