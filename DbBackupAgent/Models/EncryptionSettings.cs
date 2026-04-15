namespace DbBackupAgent.Models;

public sealed class EncryptionSettings
{
    /// <summary>Base64-encoded 32-byte AES-256 key.</summary>
    public string Key { get; init; } = string.Empty;
}
