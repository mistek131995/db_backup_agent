namespace DbBackupAgent.Models;

public sealed record FileEntry(
    string Path,
    long Size,
    long Mtime,
    int Mode,
    List<string> Chunks);

public sealed record FileManifest(
    DateTime CreatedAtUtc,
    string Database,
    string DumpObjectKey,
    List<FileEntry> Files);
