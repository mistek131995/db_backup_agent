namespace DbBackupAgent.Services;

public static class ConfigBootstrapper
{
    private const string Template = """
        {
          "Databases": [
            {
              "DatabaseType": "Postgres",
              "Host": "",
              "Port": 5432,
              "Database": "",
              "Username": "",
              "Password": "",
              "OutputPath": "/backups",
              "FilePaths": []
            }
          ],
          "EncryptionSettings": {
            "Key": ""
          },
          "UploadSettings": {
            "Provider": "S3"
          },
          "S3Settings": {
            "EndpointUrl": "",
            "AccessKey": "",
            "SecretKey": "",
            "BucketName": "",
            "Region": "us-east-1"
          },
          "SftpSettings": {
            "Host": "",
            "Port": 22,
            "Username": "",
            "Password": "",
            "PrivateKeyPath": "",
            "PrivateKeyPassphrase": "",
            "RemotePath": "/backups"
          }
        }
        """;

    /// <summary>
    /// Creates a template appsettings.json in <paramref name="configDir"/> if the file
    /// does not already exist. Safe to call on every startup.
    /// </summary>
    public static void EnsureTemplate(string configDir)
    {
        var filePath = Path.Combine(configDir, "appsettings.json");

        if (File.Exists(filePath))
            return;

        try
        {
            Directory.CreateDirectory(configDir);
            File.WriteAllText(filePath, Template);
            Console.WriteLine(
                $"[ConfigBootstrapper] Config not found. Template created at '{filePath}'. " +
                "Fill in the required fields and restart the container.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[ConfigBootstrapper] Failed to create config template at '{filePath}': {ex.Message}");
        }
    }
}
