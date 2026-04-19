namespace BackupsterAgent.Settings;

public sealed class SftpSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;

    /// <summary>Password authentication. Leave empty when using a private key.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Path to the PEM/OpenSSH private key file on the agent host.
    /// Leave empty to use password authentication.
    /// </summary>
    public string PrivateKeyPath { get; set; } = string.Empty;

    /// <summary>Passphrase for the private key file, if encrypted.</summary>
    public string PrivateKeyPassphrase { get; set; } = string.Empty;

    /// <summary>Remote base directory. Files are placed under {RemotePath}/{database}/{yyyy-MM-dd}/.</summary>
    public string RemotePath { get; set; } = "/backups";
}
