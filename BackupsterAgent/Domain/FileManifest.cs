namespace BackupsterAgent.Domain;

public sealed record FileManifest(
    DateTime CreatedAtUtc,
    string Database,
    string DumpObjectKey,
    List<FileEntry> Files);
