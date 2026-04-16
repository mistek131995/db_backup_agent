using System.Security.Cryptography;
using DbBackupAgent.Models;
using DbBackupAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbBackupAgent.Tests.Services;

[TestFixture]
public sealed class EncryptionServiceTests
{
    private const int IvLength = 16;
    private const int AesBlockLength = 16;

    private byte[] _key = null!;
    private EncryptionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _key = RandomNumberGenerator.GetBytes(32);
        var settings = Options.Create(new EncryptionSettings { Key = Convert.ToBase64String(_key) });
        _service = new EncryptionService(settings, NullLogger<EncryptionService>.Instance);
    }

    [Test]
    public void Constructor_EmptyKey_LeavesServiceUnconfigured()
    {
        var settings = Options.Create(new EncryptionSettings { Key = "" });
        var service = new EncryptionService(settings, NullLogger<EncryptionService>.Instance);

        Assert.That(service.IsConfigured, Is.False);
    }

    [Test]
    public void Constructor_KeyWithWrongLength_Throws()
    {
        var shortKey = Convert.ToBase64String(new byte[16]);
        var settings = Options.Create(new EncryptionSettings { Key = shortKey });

        Assert.Throws<InvalidOperationException>(() =>
            new EncryptionService(settings, NullLogger<EncryptionService>.Instance));
    }

    [Test]
    public void Encrypt_OutputStartsWithRandomIv()
    {
        var plaintext = new byte[] { 1, 2, 3, 4, 5 };

        var first = _service.Encrypt(plaintext);
        var second = _service.Encrypt(plaintext);

        Assert.That(first.AsSpan(0, IvLength).ToArray(), Is.Not.EqualTo(second.AsSpan(0, IvLength).ToArray()),
            "each call should generate a fresh IV");
        Assert.That(first, Is.Not.EqualTo(second),
            "different IVs must produce different ciphertexts for the same plaintext");
    }

    [Test]
    public void Encrypt_OutputLengthMatchesIvPlusPaddedPlaintext()
    {
        var plaintext = new byte[100];

        var output = _service.Encrypt(plaintext);

        var expectedCipherLength = ((plaintext.Length / AesBlockLength) + 1) * AesBlockLength;
        Assert.That(output.Length, Is.EqualTo(IvLength + expectedCipherLength));
    }

    [Test]
    public void Encrypt_RoundTripThroughAes_RecoversOriginal()
    {
        var plaintext = RandomNumberGenerator.GetBytes(12_345);

        var encrypted = _service.Encrypt(plaintext);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = _key;
        aes.IV = encrypted.AsSpan(0, IvLength).ToArray();

        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encrypted, IvLength, encrypted.Length - IvLength);

        Assert.That(decrypted, Is.EqualTo(plaintext));
    }

    [Test]
    public void Encrypt_EmptyPlaintext_ProducesOneFullPaddingBlock()
    {
        var output = _service.Encrypt([]);

        Assert.That(output.Length, Is.EqualTo(IvLength + AesBlockLength));
    }

    [Test]
    public void Encrypt_WithoutConfiguration_Throws()
    {
        var settings = Options.Create(new EncryptionSettings { Key = "" });
        var service = new EncryptionService(settings, NullLogger<EncryptionService>.Instance);

        Assert.Throws<InvalidOperationException>(() => service.Encrypt([1, 2, 3]));
    }
}
