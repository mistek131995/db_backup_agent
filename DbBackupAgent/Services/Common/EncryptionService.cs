using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using DbBackupAgent.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbBackupAgent.Services;

public sealed class EncryptionService
{
    internal const int NonceSize = 12;
    internal const int TagSize = 16;
    internal const int FrameChunkSize = 1 << 20;
    internal const int MaxFrameChunkSize = 64 << 20;
    internal static ReadOnlySpan<byte> FileMagic => [0x42, 0x4B, 0x30, 0x31];
    internal const int HeaderSize = 8;

    private readonly byte[] _key;
    private readonly ILogger<EncryptionService> _logger;

    public bool IsConfigured { get; }

    public EncryptionService(IOptions<EncryptionSettings> settings, ILogger<EncryptionService> logger)
    {
        _logger = logger;

        var keyBase64 = settings.Value.Key;
        if (string.IsNullOrWhiteSpace(keyBase64))
        {
            _logger.LogWarning("EncryptionSettings:Key is not set. Agent will not run backups until the key is configured.");
            _key = [];
            IsConfigured = false;
            return;
        }

        _key = Convert.FromBase64String(keyBase64);
        if (_key.Length != 32)
            throw new InvalidOperationException(
                $"EncryptionSettings:Key must decode to exactly 32 bytes (AES-256); got {_key.Length}");

        IsConfigured = true;
    }

    public async Task<string> EncryptAsync(string inputPath, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("EncryptionService is not configured: EncryptionSettings:Key is missing.");

        var outputPath = inputPath + ".enc";

        _logger.LogInformation("Encrypting '{InputPath}' → '{OutputPath}'", inputPath, outputPath);

        await using var inputStream = new FileStream(
            inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: true);

        await using var outputStream = new FileStream(
            outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 65536, useAsync: true);

        var header = new byte[HeaderSize];
        FileMagic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), FrameChunkSize);
        await outputStream.WriteAsync(header, ct);

        var plaintextBuffer = ArrayPool<byte>.Shared.Rent(FrameChunkSize);
        var ciphertextBuffer = ArrayPool<byte>.Shared.Rent(FrameChunkSize);
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];

        try
        {
            using var gcm = new AesGcm(_key, TagSize);

            while (true)
            {
                var read = await ReadFullAsync(inputStream, plaintextBuffer, FrameChunkSize, ct);
                if (read == 0) break;

                RandomNumberGenerator.Fill(nonce);
                gcm.Encrypt(
                    nonce,
                    plaintextBuffer.AsSpan(0, read),
                    ciphertextBuffer.AsSpan(0, read),
                    tag);

                await outputStream.WriteAsync(nonce, ct);
                await outputStream.WriteAsync(ciphertextBuffer.AsMemory(0, read), ct);
                await outputStream.WriteAsync(tag, ct);

                if (read < FrameChunkSize) break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plaintextBuffer);
            ArrayPool<byte>.Shared.Return(ciphertextBuffer);
        }

        _logger.LogInformation("Encryption completed. Output: '{OutputPath}'", outputPath);

        return outputPath;
    }

    public byte[] Encrypt(byte[] plaintext)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("EncryptionService is not configured: EncryptionSettings:Key is missing.");

        ArgumentNullException.ThrowIfNull(plaintext);

        var output = new byte[NonceSize + plaintext.Length + TagSize];
        var nonceSpan = output.AsSpan(0, NonceSize);
        var ciphertextSpan = output.AsSpan(NonceSize, plaintext.Length);
        var tagSpan = output.AsSpan(NonceSize + plaintext.Length, TagSize);

        RandomNumberGenerator.Fill(nonceSpan);

        using var gcm = new AesGcm(_key, TagSize);
        gcm.Encrypt(nonceSpan, plaintext, ciphertextSpan, tagSpan);

        return output;
    }

    public byte[] Decrypt(byte[] ciphertext)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("EncryptionService is not configured: EncryptionSettings:Key is missing.");

        ArgumentNullException.ThrowIfNull(ciphertext);

        if (ciphertext.Length < NonceSize + TagSize)
            throw new InvalidDataException(
                $"Encrypted payload is too short: {ciphertext.Length} bytes (minimum {NonceSize + TagSize}).");

        var plaintextLen = ciphertext.Length - NonceSize - TagSize;
        var plaintext = new byte[plaintextLen];

        using var gcm = new AesGcm(_key, TagSize);
        gcm.Decrypt(
            ciphertext.AsSpan(0, NonceSize),
            ciphertext.AsSpan(NonceSize, plaintextLen),
            ciphertext.AsSpan(NonceSize + plaintextLen, TagSize),
            plaintext);

        return plaintext;
    }

    public async Task DecryptAsync(string inputPath, string outputPath, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("EncryptionService is not configured: EncryptionSettings:Key is missing.");

        _logger.LogInformation("Decrypting '{InputPath}' → '{OutputPath}'", inputPath, outputPath);

        await using var inputStream = new FileStream(
            inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: true);

        await using var outputStream = new FileStream(
            outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 65536, useAsync: true);

        var header = new byte[HeaderSize];
        var headerRead = await ReadFullAsync(inputStream, header, HeaderSize, ct);
        if (headerRead < HeaderSize)
            throw new InvalidDataException("Encrypted file is truncated: header is missing or incomplete.");

        if (!header.AsSpan(0, 4).SequenceEqual(FileMagic))
            throw new InvalidDataException("Bad magic: not a Backupster encrypted file (expected BK01).");

        var frameChunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (frameChunkSize <= 0 || frameChunkSize > MaxFrameChunkSize)
            throw new InvalidDataException(
                $"Invalid frame chunk size in header: {frameChunkSize} (must be 1..{MaxFrameChunkSize}).");

        var ctTagBuffer = ArrayPool<byte>.Shared.Rent(frameChunkSize + TagSize);
        var plaintextBuffer = ArrayPool<byte>.Shared.Rent(frameChunkSize);
        var nonce = new byte[NonceSize];

        try
        {
            using var gcm = new AesGcm(_key, TagSize);

            while (true)
            {
                var nonceRead = await ReadFullAsync(inputStream, nonce, NonceSize, ct);
                if (nonceRead == 0) break;
                if (nonceRead < NonceSize)
                    throw new InvalidDataException("Encrypted file is truncated: incomplete frame nonce.");

                var ctTagRead = await ReadFullAsync(inputStream, ctTagBuffer, frameChunkSize + TagSize, ct);
                if (ctTagRead < TagSize)
                    throw new InvalidDataException("Encrypted file is truncated: incomplete frame ciphertext or tag.");

                var ctLen = ctTagRead - TagSize;

                gcm.Decrypt(
                    nonce,
                    ctTagBuffer.AsSpan(0, ctLen),
                    ctTagBuffer.AsSpan(ctLen, TagSize),
                    plaintextBuffer.AsSpan(0, ctLen));

                await outputStream.WriteAsync(plaintextBuffer.AsMemory(0, ctLen), ct);

                if (ctLen < frameChunkSize) break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(ctTagBuffer);
            ArrayPool<byte>.Shared.Return(plaintextBuffer);
        }

        _logger.LogInformation("Decryption completed. Output: '{OutputPath}'", outputPath);
    }

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
