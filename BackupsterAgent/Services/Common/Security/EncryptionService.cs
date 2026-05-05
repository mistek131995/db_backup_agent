using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using BackupsterAgent.Configuration;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Services.Common.Security;

public sealed class EncryptionService
{
    internal const int NonceSize = 12;
    internal const int TagSize = 16;
    internal const int FrameChunkSize = 1 << 20;
    internal const int MaxFrameChunkSize = 8 << 20;
    internal static ReadOnlySpan<byte> FileMagicV2 => [0x42, 0x4B, 0x30, 0x32];
    internal static ReadOnlySpan<byte> FileMagicV3 => [0x42, 0x4B, 0x30, 0x33];
    internal const int HeaderSize = 8;
    internal const int FlagSize = 1;
    internal const byte FlagFrameNonFinal = 0x00;
    internal const byte FlagFrameFinal = 0x01;
    internal const int AadSizeV2 = 4;
    internal const int AadSizeV3 = AadSizeV2 + FlagSize;

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

        await EncryptStreamAsync(inputStream, outputStream, ct);

        _logger.LogInformation("Encryption completed. Output: '{OutputPath}'", outputPath);

        return outputPath;
    }

    public async Task EncryptStreamAsync(Stream input, Stream output, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("EncryptionService is not configured: EncryptionSettings:Key is missing.");

        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var header = new byte[HeaderSize];
        FileMagicV3.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), FrameChunkSize);
        await output.WriteAsync(header, ct);

        var currentBuffer = ArrayPool<byte>.Shared.Rent(FrameChunkSize);
        var nextBuffer = ArrayPool<byte>.Shared.Rent(FrameChunkSize);
        var ciphertextBuffer = ArrayPool<byte>.Shared.Rent(FrameChunkSize);
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var aad = new byte[AadSizeV3];
        var flagBuf = new byte[FlagSize];

        try
        {
            using var gcm = new AesGcm(_key, TagSize);
            uint frameIndex = 0;

            var currentLen = await ReadFullAsync(input, currentBuffer, FrameChunkSize, ct);

            while (true)
            {
                int nextLen;
                if (currentLen < FrameChunkSize)
                    nextLen = 0;
                else
                    nextLen = await ReadFullAsync(input, nextBuffer, FrameChunkSize, ct);

                var isFinal = nextLen == 0;
                var flag = isFinal ? FlagFrameFinal : FlagFrameNonFinal;

                RandomNumberGenerator.Fill(nonce);
                BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(0, 4), frameIndex);
                aad[4] = flag;
                gcm.Encrypt(
                    nonce,
                    currentBuffer.AsSpan(0, currentLen),
                    ciphertextBuffer.AsSpan(0, currentLen),
                    tag,
                    aad);

                await output.WriteAsync(nonce, ct);
                flagBuf[0] = flag;
                await output.WriteAsync(flagBuf, ct);
                await output.WriteAsync(ciphertextBuffer.AsMemory(0, currentLen), ct);
                await output.WriteAsync(tag, ct);

                frameIndex++;
                if (isFinal) break;

                (currentBuffer, nextBuffer) = (nextBuffer, currentBuffer);
                currentLen = nextLen;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(currentBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(nextBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(ciphertextBuffer);
        }
    }

    public byte[] Encrypt(byte[] plaintext, byte[]? aad = null)
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
        gcm.Encrypt(nonceSpan, plaintext, ciphertextSpan, tagSpan, aad);

        return output;
    }

    public byte[] Decrypt(byte[] ciphertext, byte[]? aad = null)
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
            plaintext,
            aad);

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

        await DecryptStreamAsync(inputStream, outputStream, ct);

        _logger.LogInformation("Decryption completed. Output: '{OutputPath}'", outputPath);
    }

    public async Task DecryptStreamAsync(Stream input, Stream output, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("EncryptionService is not configured: EncryptionSettings:Key is missing.");

        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var header = new byte[HeaderSize];
        var headerRead = await ReadFullAsync(input, header, HeaderSize, ct);
        if (headerRead < HeaderSize)
            throw new InvalidDataException("Encrypted file is truncated: header is missing or incomplete.");

        var magic = header.AsSpan(0, 4);
        if (magic.SequenceEqual(FileMagicV3))
        {
            await DecryptStreamV3Async(input, output, header, ct);
            return;
        }
        if (magic.SequenceEqual(FileMagicV2))
        {
            await DecryptStreamV2Async(input, output, header, ct);
            return;
        }
        throw new InvalidDataException("Bad magic: not a Backupster encrypted file (expected BK02 or BK03).");
    }

    private async Task DecryptStreamV2Async(Stream input, Stream output, byte[] header, CancellationToken ct)
    {
        var frameChunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (frameChunkSize <= 0 || frameChunkSize > MaxFrameChunkSize)
            throw new InvalidDataException(
                $"Invalid frame chunk size in header: {frameChunkSize} (must be 1..{MaxFrameChunkSize}).");

        var ctTagBuffer = ArrayPool<byte>.Shared.Rent(frameChunkSize + TagSize);
        var plaintextBuffer = ArrayPool<byte>.Shared.Rent(frameChunkSize);
        var nonce = new byte[NonceSize];
        var aad = new byte[AadSizeV2];

        try
        {
            using var gcm = new AesGcm(_key, TagSize);
            uint frameIndex = 0;

            while (true)
            {
                var nonceRead = await ReadFullAsync(input, nonce, NonceSize, ct);
                if (nonceRead == 0) break;
                if (nonceRead < NonceSize)
                    throw new InvalidDataException("Encrypted file is truncated: incomplete frame nonce.");

                var ctTagRead = await ReadFullAsync(input, ctTagBuffer, frameChunkSize + TagSize, ct);
                if (ctTagRead < TagSize)
                    throw new InvalidDataException("Encrypted file is truncated: incomplete frame ciphertext or tag.");

                var ctLen = ctTagRead - TagSize;

                BinaryPrimitives.WriteUInt32BigEndian(aad, frameIndex);
                gcm.Decrypt(
                    nonce,
                    ctTagBuffer.AsSpan(0, ctLen),
                    ctTagBuffer.AsSpan(ctLen, TagSize),
                    plaintextBuffer.AsSpan(0, ctLen),
                    aad);

                await output.WriteAsync(plaintextBuffer.AsMemory(0, ctLen), ct);

                frameIndex++;
                if (ctLen < frameChunkSize) break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(ctTagBuffer);
            ArrayPool<byte>.Shared.Return(plaintextBuffer, clearArray: true);
        }
    }

    private async Task DecryptStreamV3Async(Stream input, Stream output, byte[] header, CancellationToken ct)
    {
        var frameChunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (frameChunkSize <= 0 || frameChunkSize > MaxFrameChunkSize)
            throw new InvalidDataException(
                $"Invalid frame chunk size in header: {frameChunkSize} (must be 1..{MaxFrameChunkSize}).");

        var ctTagBuffer = ArrayPool<byte>.Shared.Rent(frameChunkSize + TagSize);
        var plaintextBuffer = ArrayPool<byte>.Shared.Rent(frameChunkSize);
        var nonce = new byte[NonceSize];
        var aad = new byte[AadSizeV3];
        var flagBuf = new byte[FlagSize];
        var probeBuf = new byte[1];

        try
        {
            using var gcm = new AesGcm(_key, TagSize);
            uint frameIndex = 0;
            var seenFinal = false;

            while (true)
            {
                var nonceRead = await ReadFullAsync(input, nonce, NonceSize, ct);
                if (nonceRead == 0)
                {
                    if (!seenFinal)
                        throw new InvalidDataException(
                            "Encrypted file is truncated: no final frame marker found before EOF.");
                    break;
                }
                if (nonceRead < NonceSize)
                    throw new InvalidDataException("Encrypted file is truncated: incomplete frame nonce.");

                var flagRead = await ReadFullAsync(input, flagBuf, FlagSize, ct);
                if (flagRead < FlagSize)
                    throw new InvalidDataException("Encrypted file is truncated: incomplete frame flag byte.");

                var ctTagRead = await ReadFullAsync(input, ctTagBuffer, frameChunkSize + TagSize, ct);
                if (ctTagRead < TagSize)
                    throw new InvalidDataException("Encrypted file is truncated: incomplete frame ciphertext or tag.");

                var ctLen = ctTagRead - TagSize;

                BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(0, 4), frameIndex);
                aad[4] = flagBuf[0];
                gcm.Decrypt(
                    nonce,
                    ctTagBuffer.AsSpan(0, ctLen),
                    ctTagBuffer.AsSpan(ctLen, TagSize),
                    plaintextBuffer.AsSpan(0, ctLen),
                    aad);

                if (flagBuf[0] != FlagFrameNonFinal && flagBuf[0] != FlagFrameFinal)
                    throw new InvalidDataException(
                        $"Invalid frame flag value: 0x{flagBuf[0]:X2} (expected 0x00 or 0x01).");

                await output.WriteAsync(plaintextBuffer.AsMemory(0, ctLen), ct);

                frameIndex++;

                if (flagBuf[0] == FlagFrameFinal)
                {
                    seenFinal = true;
                    var trailing = await ReadFullAsync(input, probeBuf, 1, ct);
                    if (trailing != 0)
                        throw new InvalidDataException(
                            "Encrypted file has unexpected trailing bytes after the final frame.");
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(ctTagBuffer);
            ArrayPool<byte>.Shared.Return(plaintextBuffer, clearArray: true);
        }
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
