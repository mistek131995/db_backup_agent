using System.Buffers.Binary;
using System.Security.Cryptography;
using BackupsterAgent.Configuration;
using BackupsterAgent.Services;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Common.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Tests.Services;

[TestFixture]
public sealed class EncryptionServiceTests
{
    private const int NonceSize = EncryptionService.NonceSize;
    private const int TagSize = EncryptionService.TagSize;
    private const int FrameChunkSize = EncryptionService.FrameChunkSize;
    private const int HeaderSize = EncryptionService.HeaderSize;
    private const int FlagSize = EncryptionService.FlagSize;
    private const byte FlagFrameNonFinal = EncryptionService.FlagFrameNonFinal;
    private const byte FlagFrameFinal = EncryptionService.FlagFrameFinal;

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
    public void Encrypt_EachCallUsesFreshNonce()
    {
        var plaintext = new byte[] { 1, 2, 3, 4, 5 };

        var first = _service.Encrypt(plaintext);
        var second = _service.Encrypt(plaintext);

        Assert.That(first.AsSpan(0, NonceSize).ToArray(), Is.Not.EqualTo(second.AsSpan(0, NonceSize).ToArray()),
            "each call should generate a fresh nonce");
        Assert.That(first, Is.Not.EqualTo(second),
            "different nonces must produce different ciphertexts for the same plaintext");
    }

    [Test]
    public void Encrypt_OutputLengthIsNoncePlusPlaintextPlusTag()
    {
        var plaintext = new byte[100];

        var output = _service.Encrypt(plaintext);

        Assert.That(output.Length, Is.EqualTo(NonceSize + plaintext.Length + TagSize));
    }

    [Test]
    public void Encrypt_RoundTripThroughAesGcm_RecoversOriginal()
    {
        var plaintext = RandomNumberGenerator.GetBytes(12_345);

        var encrypted = _service.Encrypt(plaintext);
        var decrypted = DecryptBytes(encrypted, _key);

        Assert.That(decrypted, Is.EqualTo(plaintext));
    }

    [Test]
    public void Encrypt_EmptyPlaintext_ProducesNonceAndTagOnly()
    {
        var output = _service.Encrypt([]);

        Assert.That(output.Length, Is.EqualTo(NonceSize + TagSize));
    }

    [Test]
    public void Encrypt_TamperedCiphertext_FailsAuthentication()
    {
        var plaintext = RandomNumberGenerator.GetBytes(1000);
        var encrypted = _service.Encrypt(plaintext);

        encrypted[NonceSize + plaintext.Length / 2] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(() => DecryptBytes(encrypted, _key));
    }

    [Test]
    public void Encrypt_TamperedTag_FailsAuthentication()
    {
        var plaintext = RandomNumberGenerator.GetBytes(1000);
        var encrypted = _service.Encrypt(plaintext);

        encrypted[^1] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(() => DecryptBytes(encrypted, _key));
    }

    [Test]
    public void Encrypt_WithoutConfiguration_Throws()
    {
        var settings = Options.Create(new EncryptionSettings { Key = "" });
        var service = new EncryptionService(settings, NullLogger<EncryptionService>.Instance);

        Assert.Throws<InvalidOperationException>(() => service.Encrypt([1, 2, 3]));
    }

    [Test]
    public async Task EncryptAsync_SmallFile_RoundTripRecoversOriginal()
    {
        var plaintext = RandomNumberGenerator.GetBytes(12_345);
        await RoundTripFile(plaintext);
    }

    [Test]
    public async Task EncryptAsync_MultiFrameFile_RoundTripRecoversOriginal()
    {
        // 3 full frames + 1 partial
        var plaintext = RandomNumberGenerator.GetBytes(FrameChunkSize * 3 + 7_777);
        await RoundTripFile(plaintext);
    }

    [Test]
    public async Task EncryptAsync_ExactFrameBoundary_RoundTripRecoversOriginal()
    {
        var plaintext = RandomNumberGenerator.GetBytes(FrameChunkSize * 2);
        await RoundTripFile(plaintext);
    }

    [Test]
    public async Task EncryptAsync_EmptyFile_RoundTripRecoversOriginal()
    {
        await RoundTripFile([]);
    }

    [Test]
    public async Task EncryptAsync_HeaderPresentWithMagicAndChunkSize()
    {
        var inputPath = TempFile();
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3]);

        try
        {
            var outputPath = await _service.EncryptAsync(inputPath, CancellationToken.None);
            try
            {
                var bytes = await File.ReadAllBytesAsync(outputPath);

                Assert.Multiple(() =>
                {
                    Assert.That(bytes[0], Is.EqualTo((byte)0x42));
                    Assert.That(bytes[1], Is.EqualTo((byte)0x4B));
                    Assert.That(bytes[2], Is.EqualTo((byte)0x30));
                    Assert.That(bytes[3], Is.EqualTo((byte)0x33));
                    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)), Is.EqualTo((uint)FrameChunkSize));
                });
            }
            finally { SafeDelete(outputPath); }
        }
        finally { SafeDelete(inputPath); }
    }

    [Test]
    public async Task EncryptAsync_TamperedFrame_FailsAuthentication()
    {
        var plaintext = RandomNumberGenerator.GetBytes(50_000);
        var inputPath = TempFile();
        await File.WriteAllBytesAsync(inputPath, plaintext);

        try
        {
            var outputPath = await _service.EncryptAsync(inputPath, CancellationToken.None);
            try
            {
                var bytes = await File.ReadAllBytesAsync(outputPath);
                // Flip a byte inside the first frame's ciphertext (after header + nonce).
                bytes[HeaderSize + NonceSize + 10] ^= 0xFF;
                await File.WriteAllBytesAsync(outputPath, bytes);

                Assert.Throws<AuthenticationTagMismatchException>(() => DecryptFile(outputPath, _key));
            }
            finally { SafeDelete(outputPath); }
        }
        finally { SafeDelete(inputPath); }
    }

    [Test]
    public async Task EncryptAsync_WithoutConfiguration_Throws()
    {
        var settings = Options.Create(new EncryptionSettings { Key = "" });
        var service = new EncryptionService(settings, NullLogger<EncryptionService>.Instance);

        var inputPath = TempFile();
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3]);

        try
        {
            Assert.ThrowsAsync<InvalidOperationException>(
                () => service.EncryptAsync(inputPath, CancellationToken.None));
        }
        finally { SafeDelete(inputPath); }
    }

    [Test]
    public void Decrypt_RoundTripWithEncrypt_RecoversOriginal()
    {
        var plaintext = RandomNumberGenerator.GetBytes(12_345);

        var encrypted = _service.Encrypt(plaintext);
        var decrypted = _service.Decrypt(encrypted);

        Assert.That(decrypted, Is.EqualTo(plaintext));
    }

    [Test]
    public void Decrypt_EmptyPayload_ReturnsEmptyArray()
    {
        var encrypted = _service.Encrypt([]);

        var decrypted = _service.Decrypt(encrypted);

        Assert.That(decrypted, Is.Empty);
    }

    [Test]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var plaintext = RandomNumberGenerator.GetBytes(1000);
        var encrypted = _service.Encrypt(plaintext);

        encrypted[NonceSize + plaintext.Length / 2] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(() => _service.Decrypt(encrypted));
    }

    [Test]
    public void Decrypt_TooShortInput_Throws()
    {
        Assert.Throws<InvalidDataException>(() => _service.Decrypt(new byte[NonceSize + TagSize - 1]));
    }

    [Test]
    public void Decrypt_WithoutConfiguration_Throws()
    {
        var settings = Options.Create(new EncryptionSettings { Key = "" });
        var service = new EncryptionService(settings, NullLogger<EncryptionService>.Instance);

        Assert.Throws<InvalidOperationException>(() => service.Decrypt([1, 2, 3]));
    }

    [Test]
    public async Task DecryptAsync_RoundTripMultiFrame_RecoversOriginal()
    {
        var plaintext = RandomNumberGenerator.GetBytes(FrameChunkSize * 3 + 7_777);
        await RoundTripFileViaProductionDecrypt(plaintext);
    }

    [Test]
    public async Task DecryptAsync_RoundTripExactFrameBoundary_RecoversOriginal()
    {
        var plaintext = RandomNumberGenerator.GetBytes(FrameChunkSize * 2);
        await RoundTripFileViaProductionDecrypt(plaintext);
    }

    [Test]
    public async Task DecryptAsync_RoundTripEmptyFile_ProducesEmptyOutput()
    {
        await RoundTripFileViaProductionDecrypt([]);
    }

    [Test]
    public async Task DecryptAsync_BadMagic_Throws()
    {
        var inputPath = TempFile();
        var outputPath = TempFile();
        var bytes = new byte[HeaderSize];
        bytes[0] = 0xFF;
        await File.WriteAllBytesAsync(inputPath, bytes);

        try
        {
            Assert.ThrowsAsync<InvalidDataException>(
                () => _service.DecryptAsync(inputPath, outputPath, CancellationToken.None));
        }
        finally
        {
            SafeDelete(inputPath);
            SafeDelete(outputPath);
        }
    }

    [Test]
    public async Task DecryptAsync_TruncatedHeader_Throws()
    {
        var inputPath = TempFile();
        var outputPath = TempFile();
        await File.WriteAllBytesAsync(inputPath, new byte[3]);

        try
        {
            Assert.ThrowsAsync<InvalidDataException>(
                () => _service.DecryptAsync(inputPath, outputPath, CancellationToken.None));
        }
        finally
        {
            SafeDelete(inputPath);
            SafeDelete(outputPath);
        }
    }

    [Test]
    public async Task DecryptAsync_TruncatedAfterNonce_Throws()
    {
        var plaintext = RandomNumberGenerator.GetBytes(50_000);
        var inputPath = TempFile();
        await File.WriteAllBytesAsync(inputPath, plaintext);

        try
        {
            var encPath = await _service.EncryptAsync(inputPath, CancellationToken.None);
            try
            {
                var bytes = await File.ReadAllBytesAsync(encPath);
                await File.WriteAllBytesAsync(encPath, bytes.AsSpan(0, HeaderSize + NonceSize).ToArray());

                var outputPath = TempFile();
                try
                {
                    Assert.ThrowsAsync<InvalidDataException>(
                        () => _service.DecryptAsync(encPath, outputPath, CancellationToken.None));
                }
                finally { SafeDelete(outputPath); }
            }
            finally { SafeDelete(encPath); }
        }
        finally { SafeDelete(inputPath); }
    }

    [Test]
    public async Task DecryptAsync_TruncatedNonce_Throws()
    {
        var plaintext = RandomNumberGenerator.GetBytes(50_000);
        var inputPath = TempFile();
        await File.WriteAllBytesAsync(inputPath, plaintext);

        try
        {
            var encPath = await _service.EncryptAsync(inputPath, CancellationToken.None);
            try
            {
                var bytes = await File.ReadAllBytesAsync(encPath);
                await File.WriteAllBytesAsync(encPath, bytes.AsSpan(0, HeaderSize + 5).ToArray());

                var outputPath = TempFile();
                try
                {
                    Assert.ThrowsAsync<InvalidDataException>(
                        () => _service.DecryptAsync(encPath, outputPath, CancellationToken.None));
                }
                finally { SafeDelete(outputPath); }
            }
            finally { SafeDelete(encPath); }
        }
        finally { SafeDelete(inputPath); }
    }

    [Test]
    public async Task DecryptAsync_TamperedCiphertext_Throws()
    {
        var plaintext = RandomNumberGenerator.GetBytes(50_000);
        var inputPath = TempFile();
        await File.WriteAllBytesAsync(inputPath, plaintext);

        try
        {
            var encPath = await _service.EncryptAsync(inputPath, CancellationToken.None);
            try
            {
                var bytes = await File.ReadAllBytesAsync(encPath);
                bytes[HeaderSize + NonceSize + 10] ^= 0xFF;
                await File.WriteAllBytesAsync(encPath, bytes);

                var outputPath = TempFile();
                try
                {
                    Assert.ThrowsAsync<AuthenticationTagMismatchException>(
                        () => _service.DecryptAsync(encPath, outputPath, CancellationToken.None));
                }
                finally { SafeDelete(outputPath); }
            }
            finally { SafeDelete(encPath); }
        }
        finally { SafeDelete(inputPath); }
    }

    [TestCase(0)] // truncate right after header (no frames at all)
    [TestCase(1)] // truncate after 1st non-final full frame
    [TestCase(2)] // truncate after 2nd non-final full frame
    [TestCase(3)] // truncate after 3rd (last) non-final full frame, before the final partial frame
    public async Task DecryptAsync_TruncatedAtFrameBoundary_Throws(int truncateAfterFullFrames)
    {
        // 3 full non-final frames + 1 partial final frame.
        var plaintext = RandomNumberGenerator.GetBytes(FrameChunkSize * 3 + 1234);
        var inputPath = TempFile();
        await File.WriteAllBytesAsync(inputPath, plaintext);

        try
        {
            var encPath = await _service.EncryptAsync(inputPath, CancellationToken.None);
            try
            {
                var bytes = await File.ReadAllBytesAsync(encPath);
                var fullFrameOnDisk = NonceSize + FlagSize + FrameChunkSize + TagSize;
                var truncateAt = HeaderSize + truncateAfterFullFrames * fullFrameOnDisk;
                await File.WriteAllBytesAsync(encPath, bytes.AsSpan(0, truncateAt).ToArray());

                var outputPath = TempFile();
                try
                {
                    var ex = Assert.ThrowsAsync<InvalidDataException>(
                        () => _service.DecryptAsync(encPath, outputPath, CancellationToken.None));
                    Assert.That(ex!.Message, Does.Contain("final frame"));
                }
                finally { SafeDelete(outputPath); }
            }
            finally { SafeDelete(encPath); }
        }
        finally { SafeDelete(inputPath); }
    }

    [Test]
    public async Task DecryptAsync_TamperedFlag_FailsAuthentication()
    {
        var plaintext = RandomNumberGenerator.GetBytes(50_000);
        var inputPath = TempFile();
        await File.WriteAllBytesAsync(inputPath, plaintext);

        try
        {
            var encPath = await _service.EncryptAsync(inputPath, CancellationToken.None);
            try
            {
                var bytes = await File.ReadAllBytesAsync(encPath);
                // Flip the flag byte (right after the first frame's nonce).
                bytes[HeaderSize + NonceSize] ^= 0xFF;
                await File.WriteAllBytesAsync(encPath, bytes);

                var outputPath = TempFile();
                try
                {
                    Assert.ThrowsAsync<AuthenticationTagMismatchException>(
                        () => _service.DecryptAsync(encPath, outputPath, CancellationToken.None));
                }
                finally { SafeDelete(outputPath); }
            }
            finally { SafeDelete(encPath); }
        }
        finally { SafeDelete(inputPath); }
    }

    [Test]
    public async Task DecryptAsync_TrailingBytesAfterFullFinalFrame_Throws()
    {
        // Last frame is exact FrameChunkSize → ReadFullAsync stops at frame boundary,
        // probe-byte check after the loop catches the appended garbage as InvalidDataException.
        var plaintext = RandomNumberGenerator.GetBytes(FrameChunkSize);
        var inputPath = TempFile();
        await File.WriteAllBytesAsync(inputPath, plaintext);

        try
        {
            var encPath = await _service.EncryptAsync(inputPath, CancellationToken.None);
            try
            {
                var bytes = await File.ReadAllBytesAsync(encPath);
                var withTrail = new byte[bytes.Length + 1];
                bytes.CopyTo(withTrail, 0);
                withTrail[^1] = 0xAB;
                await File.WriteAllBytesAsync(encPath, withTrail);

                var outputPath = TempFile();
                try
                {
                    Assert.ThrowsAsync<InvalidDataException>(
                        () => _service.DecryptAsync(encPath, outputPath, CancellationToken.None));
                }
                finally { SafeDelete(outputPath); }
            }
            finally { SafeDelete(encPath); }
        }
        finally { SafeDelete(inputPath); }
    }

    [Test]
    public async Task DecryptAsync_TrailingBytesAfterPartialFinalFrame_Throws()
    {
        // Last frame is partial. The exact exception type depends on parser internals
        // (current behavior: trailing byte absorbed into ctTagBuffer → tag mismatch).
        // Contract under test is "file is rejected", not "rejected with this specific exception".
        var plaintext = RandomNumberGenerator.GetBytes(1234);
        var inputPath = TempFile();
        await File.WriteAllBytesAsync(inputPath, plaintext);

        try
        {
            var encPath = await _service.EncryptAsync(inputPath, CancellationToken.None);
            try
            {
                var bytes = await File.ReadAllBytesAsync(encPath);
                var withTrail = new byte[bytes.Length + 1];
                bytes.CopyTo(withTrail, 0);
                withTrail[^1] = 0xAB;
                await File.WriteAllBytesAsync(encPath, withTrail);

                var outputPath = TempFile();
                try
                {
                    Assert.That(
                        async () => await _service.DecryptAsync(encPath, outputPath, CancellationToken.None),
                        Throws.InstanceOf<InvalidDataException>()
                            .Or.InstanceOf<AuthenticationTagMismatchException>());
                }
                finally { SafeDelete(outputPath); }
            }
            finally { SafeDelete(encPath); }
        }
        finally { SafeDelete(inputPath); }
    }

    [Test]
    public async Task DecryptAsync_LegacyV2_StillReadable()
    {
        // Construct a V2 (BK02) file the way old agents wrote it: AAD = 4 bytes (frameIndex BE), no flag byte.
        const int chunkSize = 4096;
        var plaintext = RandomNumberGenerator.GetBytes(chunkSize * 2 + 500);

        var inputPath = TempFile();
        await using (var fs = File.Create(inputPath))
        {
            var header = new byte[HeaderSize];
            EncryptionService.FileMagicV2.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)chunkSize);
            await fs.WriteAsync(header);

            using var gcm = new AesGcm(_key, TagSize);
            var pos = 0;
            uint frameIndex = 0;
            var aad = new byte[4];
            while (pos < plaintext.Length)
            {
                var chunk = Math.Min(chunkSize, plaintext.Length - pos);
                var nonce = new byte[NonceSize];
                var ct = new byte[chunk];
                var tag = new byte[TagSize];
                RandomNumberGenerator.Fill(nonce);
                BinaryPrimitives.WriteUInt32BigEndian(aad, frameIndex);
                gcm.Encrypt(nonce, plaintext.AsSpan(pos, chunk), ct, tag, aad);
                await fs.WriteAsync(nonce);
                await fs.WriteAsync(ct);
                await fs.WriteAsync(tag);
                pos += chunk;
                frameIndex++;
            }
        }

        try
        {
            var outputPath = TempFile();
            try
            {
                await _service.DecryptAsync(inputPath, outputPath, CancellationToken.None);
                var decrypted = await File.ReadAllBytesAsync(outputPath);
                Assert.That(decrypted, Is.EqualTo(plaintext));
            }
            finally { SafeDelete(outputPath); }
        }
        finally { SafeDelete(inputPath); }
    }

    [Test]
    public async Task DecryptAsync_LegacyV2_NonStandardChunkSize_ReadsSizeFromHeader()
    {
        const int customChunkSize = 4096;
        var plaintext = RandomNumberGenerator.GetBytes(customChunkSize * 2 + 500);

        var inputPath = TempFile();
        await using (var fs = File.Create(inputPath))
        {
            var header = new byte[HeaderSize];
            EncryptionService.FileMagicV2.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)customChunkSize);
            await fs.WriteAsync(header);

            using var gcm = new AesGcm(_key, TagSize);
            var pos = 0;
            uint frameIndex = 0;
            var aad = new byte[4];
            while (pos < plaintext.Length)
            {
                var chunk = Math.Min(customChunkSize, plaintext.Length - pos);
                var nonce = new byte[NonceSize];
                var ct = new byte[chunk];
                var tag = new byte[TagSize];
                RandomNumberGenerator.Fill(nonce);
                BinaryPrimitives.WriteUInt32BigEndian(aad, frameIndex);
                gcm.Encrypt(nonce, plaintext.AsSpan(pos, chunk), ct, tag, aad);
                await fs.WriteAsync(nonce);
                await fs.WriteAsync(ct);
                await fs.WriteAsync(tag);
                pos += chunk;
                frameIndex++;
            }
        }

        try
        {
            var outputPath = TempFile();
            try
            {
                await _service.DecryptAsync(inputPath, outputPath, CancellationToken.None);
                var decrypted = await File.ReadAllBytesAsync(outputPath);
                Assert.That(decrypted, Is.EqualTo(plaintext));
            }
            finally { SafeDelete(outputPath); }
        }
        finally { SafeDelete(inputPath); }
    }

    [Test]
    public async Task DecryptAsync_OversizedChunkSizeInHeader_Throws()
    {
        var inputPath = TempFile();
        var outputPath = TempFile();
        var header = new byte[HeaderSize];
        EncryptionService.FileMagicV2.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), uint.MaxValue);
        await File.WriteAllBytesAsync(inputPath, header);

        try
        {
            Assert.ThrowsAsync<InvalidDataException>(
                () => _service.DecryptAsync(inputPath, outputPath, CancellationToken.None));
        }
        finally
        {
            SafeDelete(inputPath);
            SafeDelete(outputPath);
        }
    }

    [Test]
    public async Task DecryptAsync_WithoutConfiguration_Throws()
    {
        var settings = Options.Create(new EncryptionSettings { Key = "" });
        var service = new EncryptionService(settings, NullLogger<EncryptionService>.Instance);

        var inputPath = TempFile();
        var outputPath = TempFile();
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3]);

        try
        {
            Assert.ThrowsAsync<InvalidOperationException>(
                () => service.DecryptAsync(inputPath, outputPath, CancellationToken.None));
        }
        finally
        {
            SafeDelete(inputPath);
            SafeDelete(outputPath);
        }
    }

    private async Task RoundTripFileViaProductionDecrypt(byte[] plaintext)
    {
        var inputPath = TempFile();
        await File.WriteAllBytesAsync(inputPath, plaintext);

        try
        {
            var encPath = await _service.EncryptAsync(inputPath, CancellationToken.None);
            try
            {
                var outputPath = TempFile();
                try
                {
                    await _service.DecryptAsync(encPath, outputPath, CancellationToken.None);
                    var decrypted = await File.ReadAllBytesAsync(outputPath);
                    Assert.That(decrypted, Is.EqualTo(plaintext));
                }
                finally { SafeDelete(outputPath); }
            }
            finally { SafeDelete(encPath); }
        }
        finally { SafeDelete(inputPath); }
    }

    private async Task RoundTripFile(byte[] plaintext)
    {
        var inputPath = TempFile();
        await File.WriteAllBytesAsync(inputPath, plaintext);

        try
        {
            var outputPath = await _service.EncryptAsync(inputPath, CancellationToken.None);
            try
            {
                var decrypted = DecryptFile(outputPath, _key);
                Assert.That(decrypted, Is.EqualTo(plaintext));
            }
            finally { SafeDelete(outputPath); }
        }
        finally { SafeDelete(inputPath); }
    }

    internal static byte[] DecryptBytes(byte[] input, byte[] key, byte[]? aad = null)
    {
        using var gcm = new AesGcm(key, TagSize);
        var plaintextLen = input.Length - NonceSize - TagSize;
        var plaintext = new byte[plaintextLen];
        var nonce = input.AsSpan(0, NonceSize);
        var ct = input.AsSpan(NonceSize, plaintextLen);
        var tag = input.AsSpan(NonceSize + plaintextLen, TagSize);
        gcm.Decrypt(nonce, ct, tag, plaintext, aad);
        return plaintext;
    }

    internal static byte[] DecryptFile(string path, byte[] key)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length < HeaderSize)
            throw new InvalidDataException("encrypted file too short");
        if (bytes[0] != 0x42 || bytes[1] != 0x4B || bytes[2] != 0x30 || bytes[3] != 0x33)
            throw new InvalidDataException("bad magic (expected BK03)");

        var chunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        var fullFrameSize = NonceSize + FlagSize + chunkSize + TagSize;

        using var gcm = new AesGcm(key, TagSize);
        using var output = new MemoryStream();
        var offset = HeaderSize;
        uint frameIndex = 0;
        var aad = new byte[5];
        var seenFinal = false;

        while (offset < bytes.Length)
        {
            var remaining = bytes.Length - offset;
            var ctSize = remaining >= fullFrameSize ? chunkSize : remaining - NonceSize - FlagSize - TagSize;
            if (ctSize < 0)
                throw new InvalidDataException("truncated frame");

            var nonce = bytes.AsSpan(offset, NonceSize);
            var flag = bytes[offset + NonceSize];
            if (flag != FlagFrameNonFinal && flag != FlagFrameFinal)
                throw new InvalidDataException($"invalid flag value: 0x{flag:X2} (expected 0x00 or 0x01)");
            var ct = bytes.AsSpan(offset + NonceSize + FlagSize, ctSize);
            var tag = bytes.AsSpan(offset + NonceSize + FlagSize + ctSize, TagSize);
            var plaintext = new byte[ctSize];
            BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(0, 4), frameIndex);
            aad[4] = flag;
            gcm.Decrypt(nonce, ct, tag, plaintext, aad);
            output.Write(plaintext);

            offset += NonceSize + FlagSize + ctSize + TagSize;
            frameIndex++;

            if (flag == FlagFrameFinal) { seenFinal = true; break; }
        }

        if (!seenFinal)
            throw new InvalidDataException("truncated: no final frame marker");
        if (offset != bytes.Length)
            throw new InvalidDataException("trailing bytes after final frame");

        return output.ToArray();
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"enc-test-{Guid.NewGuid():N}.bin");

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
