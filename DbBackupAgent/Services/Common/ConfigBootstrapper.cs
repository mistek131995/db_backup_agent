namespace DbBackupAgent.Services.Common;

public static class ConfigBootstrapper
{
    private const string Template = """
        {
          "Connections": [
            {
              "Name": "main",
              "DatabaseType": "Postgres",
              "Host": "",
              "Port": 5432,
              "Username": "",
              "Password": ""
            }
          ],
          "Storages": [
            {
              "Name": "main",
              "Provider": "S3",
              "S3": {
                "EndpointUrl": "",
                "AccessKey": "",
                "SecretKey": "",
                "BucketName": "",
                "Region": "us-east-1"
              }
            }
          ],
          "Databases": [
            {
              "ConnectionName": "main",
              "StorageName": "main",
              "Database": "",
              "OutputPath": "/backups",
              "FilePaths": []
            }
          ],
          "EncryptionSettings": {
            "Key": ""
          }
        }
        """;

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
