using System.Security.Cryptography;
using DbBackupAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbBackupAgent.Services;

public sealed class EncryptionService
{
    private readonly byte[] _key;
    private readonly ILogger<EncryptionService> _logger;

    public EncryptionService(IOptions<EncryptionSettings> settings, ILogger<EncryptionService> logger)
    {
        _logger = logger;

        var keyBase64 = settings.Value.Key;
        if (string.IsNullOrWhiteSpace(keyBase64))
            throw new InvalidOperationException("EncryptionSettings:Key is required");

        _key = Convert.FromBase64String(keyBase64);
        if (_key.Length != 32)
            throw new InvalidOperationException(
                $"EncryptionSettings:Key must decode to exactly 32 bytes (AES-256); got {_key.Length}");
    }

    /// <summary>
    /// Encrypts <paramref name="inputPath"/> with AES-256-CBC and writes the result to
    /// <c>{inputPath}.enc</c>. The randomly generated IV (16 bytes) is prepended to the
    /// ciphertext so the decryptor can read it back without separate storage.
    /// The original file is NOT deleted — cleanup is the caller's responsibility.
    /// </summary>
    /// <returns>Path of the encrypted output file.</returns>
    public async Task<string> EncryptAsync(string inputPath, CancellationToken ct)
    {
        var outputPath = inputPath + ".enc";

        _logger.LogInformation("Encrypting '{InputPath}' → '{OutputPath}'", inputPath, outputPath);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = _key;
        aes.GenerateIV(); // cryptographically random 16-byte IV

        await using var inputStream = new FileStream(
            inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: true);

        await using var outputStream = new FileStream(
            outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 65536, useAsync: true);

        // First 16 bytes of the output file = IV (plaintext, not secret)
        await outputStream.WriteAsync(aes.IV, ct);

        // leaveOpen: true so we can flush outputStream normally after CryptoStream is done
        await using var cryptoStream = new CryptoStream(
            outputStream, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true);

        await inputStream.CopyToAsync(cryptoStream, ct);

        // Write the final PKCS7 padding block with the caller's CT before dispose
        await cryptoStream.FlushFinalBlockAsync(ct);

        // DisposeAsync will not call FlushFinalBlock again since it was already called
        _logger.LogInformation("Encryption completed. Output: '{OutputPath}'", outputPath);

        return outputPath;
    }
}
