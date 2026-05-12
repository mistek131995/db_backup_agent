using Amazon.S3;
using Amazon.S3.Model;
using BackupsterAgent.Configuration;
using BackupsterAgent.Providers.Upload;
using Microsoft.Extensions.Configuration;

namespace BackupsterAgent.IntegrationTests;

public static class IntegrationConfig
{
    private static readonly Lazy<IConfiguration> Config = new(BuildConfiguration);

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddUserSecrets<IntegrationConfigMarker>(optional: true)
            .AddEnvironmentVariables("BACKUPSTER_INTEGRATION_")
            .Build();

    public static bool TryGetWebDavSettings(out WebDavSettings settings)
    {
        settings = new WebDavSettings();
        Config.Value.GetSection("WebDav").Bind(settings);

        return !string.IsNullOrWhiteSpace(settings.BaseUrl)
            && !string.IsNullOrWhiteSpace(settings.Username)
            && !string.IsNullOrWhiteSpace(settings.Password)
            && !string.IsNullOrWhiteSpace(settings.RemotePath);
    }

    public static bool TryGetBackupSourcePath(out string path)
    {
        path = Config.Value["Backup:SourcePath"] ?? string.Empty;
        return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
    }

    public static bool TryGetSftpSettings(out SftpSettings settings)
    {
        settings = new SftpSettings();
        Config.Value.GetSection("Sftp").Bind(settings);

        return !string.IsNullOrWhiteSpace(settings.Host)
            && !string.IsNullOrWhiteSpace(settings.Username)
            && !string.IsNullOrWhiteSpace(settings.Password)
            && !string.IsNullOrWhiteSpace(settings.RemotePath)
            && settings.Port > 0;
    }

    public static bool TryGetS3Settings(out S3Settings settings)
    {
        var section = Config.Value.GetSection("S3");
        var endpointUrl = section["EndpointUrl"] ?? string.Empty;
        var accessKey = section["AccessKey"] ?? string.Empty;
        var secretKey = section["SecretKey"] ?? string.Empty;
        var region = string.IsNullOrWhiteSpace(section["Region"]) ? "us-east-1" : section["Region"]!;

        settings = new S3Settings
        {
            EndpointUrl = endpointUrl,
            AccessKey = accessKey,
            SecretKey = secretKey,
            Region = region,
            BucketName = string.Empty,
        };

        return !string.IsNullOrWhiteSpace(endpointUrl)
            && !string.IsNullOrWhiteSpace(accessKey)
            && !string.IsNullOrWhiteSpace(secretKey);
    }

    public static string MakeBucketName() =>
        $"backupster-itest-{Guid.NewGuid():N}";

    public static async Task EnsureBucketAsync(S3Settings settings, string bucketName, CancellationToken ct)
    {
        using var client = CreateS3Client(settings);
        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucketName }, ct);
        }
        catch (AmazonS3Exception ex) when (
            ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // idempotent
        }
    }

    public static async Task DeleteBucketRecursiveAsync(S3Settings settings, string bucketName, CancellationToken ct)
    {
        using var client = CreateS3Client(settings);

        string? continuationToken = null;
        do
        {
            ListObjectsV2Response listResp;
            try
            {
                listResp = await client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucketName,
                    ContinuationToken = continuationToken,
                    MaxKeys = 1000,
                }, ct);
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket")
            {
                return;
            }

            if (listResp.S3Objects is { Count: > 0 } objects)
            {
                await client.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = bucketName,
                    Objects = objects.Select(o => new KeyVersion { Key = o.Key }).ToList(),
                    Quiet = true,
                }, ct);
            }

            continuationToken = (listResp.IsTruncated ?? false) ? listResp.NextContinuationToken : null;
        } while (continuationToken is not null);

        try
        {
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucketName }, ct);
        }
        catch (AmazonS3Exception)
        {
            // best-effort: bucket might still have multipart uploads or versioned objects
        }
    }

    private static AmazonS3Client CreateS3Client(S3Settings settings)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = settings.EndpointUrl,
            ForcePathStyle = true,
            AuthenticationRegion = settings.Region,
        };
        return new AmazonS3Client(settings.AccessKey, settings.SecretKey, config);
    }

    public static string MakeUniquePrefix(string testClass) =>
        $"integration-{testClass}-{Guid.NewGuid():N}";

    public static async Task CleanupPrefixAsync(IUploadProvider provider, string prefix, CancellationToken ct)
    {
        var keys = new List<string>();
        await foreach (var obj in provider.ListAsync(prefix, ct))
            keys.Add(obj.Key);

        foreach (var key in keys)
        {
            try { await provider.DeleteAsync(key, ct); }
            catch { /* best-effort */ }
        }
    }
}

internal sealed class IntegrationConfigMarker
{
}
