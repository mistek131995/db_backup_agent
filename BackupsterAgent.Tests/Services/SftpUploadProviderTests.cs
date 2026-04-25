using System.Security.Cryptography;
using BackupsterAgent.Configuration;
using BackupsterAgent.Providers.Upload;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupsterAgent.Tests.Services;

[TestFixture]
public sealed class SftpUploadProviderTests
{
    [Test]
    public void ComputeFingerprint_MatchesOpensshFormat()
    {
        var hostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIPublicKeyBytesExampleHere"u8.ToArray();
        var expected = "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');

        var actual = SftpUploadProvider.ComputeFingerprint(hostKey);

        Assert.That(actual, Is.EqualTo(expected));
        Assert.That(actual, Does.StartWith("SHA256:"));
        Assert.That(actual, Does.Not.EndWith("="), "openssh-style fingerprint has no base64 padding");
    }

    [Test]
    public void ComputeFingerprint_DifferentKeys_ProduceDifferentFingerprints()
    {
        var a = SftpUploadProvider.ComputeFingerprint([1, 2, 3]);
        var b = SftpUploadProvider.ComputeFingerprint([1, 2, 4]);
        Assert.That(a, Is.Not.EqualTo(b));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void DownloadAsync_NullOrWhitespaceObjectKey_Throws(string? objectKey)
    {
        var provider = NewProvider();
        Assert.That(
            async () => await provider.DownloadAsync(objectKey!, "/tmp/x", progress: null, CancellationToken.None),
            Throws.InstanceOf<ArgumentException>());
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void DownloadAsync_NullOrWhitespaceLocalPath_Throws(string? localPath)
    {
        var provider = NewProvider();
        Assert.That(
            async () => await provider.DownloadAsync("dump.enc", localPath!, progress: null, CancellationToken.None),
            Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void DownloadBytesAsync_Throws_WithMessageMentioningS3()
    {
        var provider = NewProvider();
        var ex = Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.DownloadBytesAsync("k", CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("S3-only"));
    }

    [Test]
    public void ExistsAsync_Throws()
    {
        var provider = NewProvider();
        Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.ExistsAsync("k", CancellationToken.None));
    }

    [Test]
    public void UploadBytesAsync_Throws()
    {
        var provider = NewProvider();
        Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.UploadBytesAsync([1, 2, 3], "k", CancellationToken.None));
    }

    [Test]
    public void ListAsync_Throws()
    {
        var provider = NewProvider();
        Assert.That(
            async () =>
            {
                await foreach (var _ in provider.ListAsync("prefix", CancellationToken.None))
                {
                }
            },
            Throws.InstanceOf<NotSupportedException>());
    }

    private static SftpUploadProvider NewProvider() =>
        new(new SftpSettings { Host = "sftp.example", RemotePath = "/backups" },
            NullLogger<SftpUploadProvider>.Instance);
}
