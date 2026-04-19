using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackupsterAgent.Domain;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Upload;
using BackupsterAgent.Settings;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Services.Restore;

public sealed class FileRestoreService
{
    private const int MaxErrorMessageLength = 2000;
    private const int MaxReportedPerFileErrors = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly EncryptionService _encryption;
    private readonly RestoreSettings _restoreSettings;
    private readonly ILogger<FileRestoreService> _logger;

    public FileRestoreService(
        EncryptionService encryption,
        IOptions<RestoreSettings> restoreSettings,
        ILogger<FileRestoreService> logger)
    {
        _encryption = encryption;
        _restoreSettings = restoreSettings.Value;
        _logger = logger;
    }

    public async Task<FileRestoreResult> RunAsync(
        string manifestKey,
        string? targetFileRoot,
        IUploadService uploader,
        IProgressReporter<RestoreStage> reporter,
        CancellationToken ct)
    {
        reporter.Report(RestoreStage.DownloadingManifest);

        FileManifest manifest;
        try
        {
            var encrypted = await uploader.DownloadBytesAsync(manifestKey, ct);
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

        string baseDir;
        bool isAgentLandingZone;
        try
        {
            (baseDir, isAgentLandingZone) = ResolveBaseDir(targetFileRoot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FileRestoreService: failed to resolve base directory");
            return FileRestoreResult.Failed(
                $"Не удалось определить папку для восстановления файлов: {ex.Message}");
        }

        if (isAgentLandingZone)
        {
            try
            {
                ResetDirectory(baseDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FileRestoreService: failed to reset landing zone '{Base}'", baseDir);
                return FileRestoreResult.Failed(
                    $"Не удалось очистить служебную папку восстановления '{baseDir}': {ex.Message}");
            }
        }

        _logger.LogInformation(
            "FileRestoreService: restoring {Count} file(s) from manifest '{ManifestKey}' into '{Base}' (landingZone={Landing})",
            manifest.Files.Count, manifestKey, baseDir, isAgentLandingZone);

        var restored = 0;
        var failed = new List<(string Path, string Reason)>();
        var totalFiles = manifest.Files.Count;
        var index = 0;

        foreach (var entry in manifest.Files)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

            reporter.Report(
                RestoreStage.RestoringFiles,
                processed: index,
                total: totalFiles,
                unit: "files",
                currentItem: entry.Path);
            index++;

            try
            {
                await RestoreFileAsync(entry, baseDir, uploader, ct);
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

    private async Task RestoreFileAsync(FileEntry entry, string baseDir, IUploadService uploader, CancellationToken ct)
    {
        var targetPath = ResolveTargetPathSafe(baseDir, entry.Path);
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

                    var encrypted = await uploader.DownloadBytesAsync($"chunks/{chunkSha}", ct);
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

            File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            SafeDelete(tmpPath);
            throw;
        }
    }

    private (string BaseDir, bool IsAgentLandingZone) ResolveBaseDir(string? targetFileRoot)
    {
        if (!string.IsNullOrWhiteSpace(targetFileRoot))
        {
            var absolute = Path.GetFullPath(targetFileRoot);
            return (absolute, false);
        }

        var raw = string.IsNullOrWhiteSpace(_restoreSettings.FileRestoreBasePath)
            ? "./restore-files"
            : _restoreSettings.FileRestoreBasePath;

        var defaultBase = Path.IsPathRooted(raw)
            ? Path.GetFullPath(raw)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, raw));

        return (defaultBase, true);
    }

    private void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    internal static string ResolveTargetPathSafe(string baseDir, string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
            throw new InvalidDataException("В манифесте пустой путь файла.");

        if (entryPath.Contains('\0'))
            throw new InvalidDataException("В манифесте путь содержит NUL-байт.");

        var normalized = entryPath.Replace('\\', '/');

        if (normalized.StartsWith('/'))
            throw new InvalidDataException($"В манифесте абсолютный путь '{entryPath}' — ожидался относительный.");

        if (normalized.Length >= 2 && normalized[1] == ':')
            throw new InvalidDataException($"В манифесте путь с буквой диска '{entryPath}' — ожидался относительный.");

        foreach (var segment in normalized.Split('/'))
        {
            if (segment == "..")
                throw new InvalidDataException($"В манифесте путь выходит за пределы базовой папки: '{entryPath}'.");
        }

        var rel = normalized.Replace('/', Path.DirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(baseDir, rel));
        var baseFull = Path.GetFullPath(baseDir);

        var baseWithSep = baseFull.EndsWith(Path.DirectorySeparatorChar)
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(baseWithSep, StringComparison.Ordinal) && combined != baseFull)
            throw new InvalidDataException($"Путь '{entryPath}' резолвится за пределы '{baseDir}'.");

        return combined;
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
