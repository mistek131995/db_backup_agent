namespace BackupsterAgent.Contracts;

public sealed class MarkStorageUnreachableDto
{
    public IReadOnlyList<Guid> Ids { get; init; } = Array.Empty<Guid>();
}
