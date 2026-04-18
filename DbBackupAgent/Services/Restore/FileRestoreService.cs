using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbBackupAgent.Domain;
using Microsoft.Extensions.Logging;

namespace DbBackupAgent.Services;

public sealed class FileRestoreService
{
    private const int MaxErrorMessageLength = 2000;
    private const int MaxReportedPerFileErrors = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly EncryptionService _encryption;
    private readonly S3UploadService _s3;
    private readonly ILogger<FileRestoreService> _logger;

    public FileRestoreService(
        EncryptionService encryption,
        S3UploadService s3,
        ILogger<FileRestoreService> logger)
    {
        _encryption = encryption;
        _s3 = s3;
        _logger = logger;
    }

    public async Task<FileRestoreResult> RunAsync(
        string manifestKey,
        string? targetFileRoot,
        CancellationToken ct)
    {
        FileManifest manifest;
        try
        {
            var encrypted = await _s3.DownloadBytesAsync(manifestKey, ct);
            var json = _encryption.Decrypt(encrypted);
            manifest = JsonSerializer.Deserialize<FileManifest>(json, JsonOptions)
                ?? throw new InvalidDataException("Manifest JSON deserialized to null.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            _logger.LogError(ex, "FileRestoreService: manifest auth tag mismatch ({ManifestKey})", manifestKey);
            return FileRestoreResult.Failed(
                $"Не удалось расшифровать манифест файлов ('{manifestKey}'). " +
                "Вероятные причины: EncryptionKey агента изменился после создания бэкапа, " +
                "или файл повреждён в хранилище.");
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "FileRestoreService: cryptographic error for manifest ({ManifestKey})", manifestKey);
            return FileRestoreResult.Failed(
                $"Ошибка криптографии при расшифровке манифеста файлов ('{manifestKey}'). " +
                "Проверьте EncryptionKey агента и целостность файла в хранилище.");
        }
        catch (InvalidDataException ex)
        {
            _logger.LogError(ex, "FileRestoreService: invalid manifest data ({ManifestKey})", manifestKey);
            return FileRestoreResult.Failed(
                $"Манифест файлов '{manifestKey}' повреждён или имеет неподдерживаемый формат.");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "FileRestoreService: manifest JSON parse failed ({ManifestKey})", manifestKey);
            return FileRestoreResult.Failed(
                $"Манифест файлов '{manifestKey}' не удалось разобрать как JSON.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FileRestoreService: failed to fetch manifest ({ManifestKey})", manifestKey);
            return FileRestoreResult.Failed(
                $"Не удалось получить манифест файлов '{manifestKey}': {ex.Message}");
        }

        if (manifest.Files.Count == 0)
        {
            _logger.LogInformation("FileRestoreService: manifest {ManifestKey} contains no files", manifestKey);
            return FileRestoreResult.Success(0);
        }

        _logger.LogInformation(
            "FileRestoreService: restoring {Count} file(s) from manifest '{ManifestKey}', targetRoot: '{Root}'",
            manifest.Files.Count, manifestKey, targetFileRoot ?? "(absolute paths)");

        var restored = 0;
        var failed = new List<(string Path, string Reason)>();

        foreach (var entry in manifest.Files)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

            try
            {
                await RestoreFileAsync(entry, targetFileRoot, ct);
                restored++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FileRestoreService: failed to restore file '{Path}'", entry.Path);
                failed.Add((entry.Path, ClassifyFileError(ex)));
            }
        }

        if (failed.Count == 0)
        {
            _logger.LogInformation(
                "FileRestoreService: all {Count} file(s) restored successfully",
                restored);
            return FileRestoreResult.Success(restored);
        }

        var message = BuildPartialErrorMessage(failed);
        _logger.LogWarning(
            "FileRestoreService: partial restore — {Restored} succeeded, {Failed} failed",
            restored, failed.Count);
        return FileRestoreResult.Partial(restored, failed.Count, message);
    }

    private async Task RestoreFileAsync(FileEntry entry, string? targetFileRoot, CancellationToken ct)
    {
        var targetPath = ResolveTargetPath(entry.Path, targetFileRoot);
        var targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
            Directory.CreateDirectory(targetDir);

        var tmpPath = targetPath + ".restore-tmp";

        try
        {
            await using (var stream = new FileStream(
                tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 65536, useAsync: true))
            {
                foreach (var chunkSha in entry.Chunks)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

                    var encrypted = await _s3.DownloadBytesAsync($"chunks/{chunkSha}", ct);
                    var plaintext = _encryption.Decrypt(encrypted);
                    await stream.WriteAsync(plaintext, ct);
                }
            }

            var actualSize = new FileInfo(tmpPath).Length;
            if (actualSize != entry.Size)
            {
                throw new InvalidDataException(
                    $"Итоговый размер {actualSize} байт не совпадает с ожидаемым {entry.Size} байт из манифеста.");
            }

            ApplyMetadata(tmpPath, entry);

            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tmpPath, targetPath);
        }
        catch
        {
            SafeDelete(tmpPath);
            throw;
        }
    }

    private static string ResolveTargetPath(string sourcePath, string? targetFileRoot)
    {
        if (string.IsNullOrWhiteSpace(targetFileRoot))
            return sourcePath;

        var relative = NormalizeForRelative(sourcePath);
        return Path.Combine(targetFileRoot, relative);
    }

    private static string NormalizeForRelative(string absolutePath)
    {
        if (absolutePath.Length >= 2 && absolutePath[1] == ':')
        {
            var drive = absolutePath[0];
            var rest = absolutePath.Length > 2 ? absolutePath.Substring(2) : string.Empty;
            rest = rest.TrimStart('\\', '/');
            var head = drive + Path.DirectorySeparatorChar.ToString();
            return head + rest.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        }

        var trimmed = absolutePath.TrimStart('/', '\\');
        return trimmed.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    }

    private void ApplyMetadata(string path, FileEntry entry)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeSeconds(entry.Mtime).UtcDateTime);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileRestoreService: failed to set mtime for '{Path}'", path);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                File.SetUnixFileMode(path, (UnixFileMode)(entry.Mode & 0x1FF));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FileRestoreService: failed to set mode for '{Path}'", path);
            }
        }
    }

    private static string ClassifyFileError(Exception ex) => ex switch
    {
        FileNotFoundException => "отсутствует чанк в хранилище",
        AuthenticationTagMismatchException => "ошибка расшифровки чанка (неверный ключ или повреждение)",
        CryptographicException => "ошибка криптографии чанка",
        InvalidDataException e => e.Message,
        _ => ex.Message,
    };

    private static string BuildPartialErrorMessage(List<(string Path, string Reason)> failed)
    {
        var sb = new StringBuilder();
        sb.Append($"Не удалось восстановить {failed.Count} файл(ов). ");

        var toShow = Math.Min(failed.Count, MaxReportedPerFileErrors);
        for (int i = 0; i < toShow; i++)
        {
            var (path, reason) = failed[i];
            sb.Append($"\n  - '{path}': {reason}");
        }

        if (failed.Count > toShow)
            sb.Append($"\n  ... и ещё {failed.Count - toShow} ошибок (см. логи агента)");

        var message = sb.ToString();
        if (message.Length > MaxErrorMessageLength)
            message = message.Substring(0, MaxErrorMessageLength - 32) + "... (обрезано, см. логи агента)";
        return message;
    }

    private void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileRestoreService: failed to delete temp '{Path}'", path);
        }
    }
}
