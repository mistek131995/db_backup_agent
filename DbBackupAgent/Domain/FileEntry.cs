namespace DbBackupAgent.Domain;

public sealed record FileEntry(
    string Path,
    long Size,
    long Mtime,
    int Mode,
    List<string> Chunks);
