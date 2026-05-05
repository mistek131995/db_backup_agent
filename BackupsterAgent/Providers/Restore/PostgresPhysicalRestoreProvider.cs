using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using BackupsterAgent.Configuration;
using BackupsterAgent.Exceptions;
using BackupsterAgent.Services.Common.Resolvers;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BackupsterAgent.Providers.Restore;

public sealed class PostgresPhysicalRestoreProvider : IRestoreProvider
{
    private const string MarkerFileName = ".backupster-marker";
    private const int OrphanGraceHours = 48;

    private readonly ILogger<PostgresPhysicalRestoreProvider> _logger;
    private readonly PostgresBinaryResolver _binaryResolver;
    private readonly RestoreSettings _restoreSettings;

    private string? _pgDataPath;
    private string? _pgCtlPath;

    public PostgresPhysicalRestoreProvider(
        ILogger<PostgresPhysicalRestoreProvider> logger,
        PostgresBinaryResolver binaryResolver,
        IOptions<RestoreSettings> restoreSettings)
    {
        _logger = logger;
        _binaryResolver = binaryResolver;
        _restoreSettings = restoreSettings.Value;
    }

    public async Task ValidatePermissionsAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct)
    {
        _pgCtlPath = await _binaryResolver.ResolveAsync(connection, "pg_ctl", ct);
        await CheckPgCtlAsync(_pgCtlPath, ct);

        _pgDataPath = await QueryDataDirectoryAsync(connection, ct);
        _logger.LogInformation("Resolved PGDATA from cluster: '{PgDataPath}'", _pgDataPath);

        if (!Directory.Exists(_pgDataPath))
            throw new RestorePermissionException(
                $"Каталог PGDATA '{_pgDataPath}' недоступен на хосте агента. " +
                "Физическое восстановление требует, чтобы агент и PostgreSQL выполнялись на одном хосте.");

        var realPgDataPath = ResolveRealPath(_pgDataPath);
        if (!string.Equals(realPgDataPath, _pgDataPath, StringComparison.Ordinal))
            _logger.LogInformation(
                "PGDATA '{PgDataPath}' resolves to real path '{RealPath}'. " +
                "Staging/swap operations during restore will use the real parent directory.",
                _pgDataPath, realPgDataPath);

        var (parent, _) = SplitPgDataPath(realPgDataPath);
        EnsureSameFsRename(parent, realPgDataPath);

        await EnsureClusterIsNotServiceManagedAsync(_pgDataPath, ct);
    }

    public Task ValidateRestoreSourceAsync(ConnectionConfig connection, string restoreFilePath, CancellationToken ct) =>
        Task.CompletedTask;

    public Task PrepareTargetDatabaseAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public async Task RestoreAsync(ConnectionConfig connection, string targetDatabase, string restoreFilePath, CancellationToken ct)
    {
        var pgDataPath = _pgDataPath!;
        var pgCtl = _pgCtlPath ?? await _binaryResolver.ResolveAsync(connection, "pg_ctl", ct);

        var realPgDataPath = ResolveRealPath(pgDataPath);
        var (parent, leaf) = SplitPgDataPath(realPgDataPath);

        CleanupOrphanStagingDirs(parent, leaf);

        var guid = Guid.NewGuid().ToString("N")[..8];
        var stagingPath = Path.Combine(parent, $"{leaf}.new-{guid}");
        var oldPath = Path.Combine(parent, $"{leaf}.old-{guid}");
        var failedPath = Path.Combine(parent, $"{leaf}.failed-{guid}");
        var startLog = Path.Combine(Path.GetTempPath(), $"backupster-pg-start-{Guid.NewGuid():N}.log");

        Directory.CreateDirectory(stagingPath);
        WriteMarkerFile(stagingPath);

        try
        {
            try
            {
                _logger.LogInformation(
                    "Extracting base archive '{ArchivePath}' to staging '{StagingPath}'", restoreFilePath, stagingPath);
                await ExtractTarGzAsync(restoreFilePath, stagingPath, ct);
                await VerifyStagedClusterAsync(stagingPath, pgCtl, ct);
                _logger.LogInformation("Staged cluster verified at '{StagingPath}'", stagingPath);

                EnsureSameFsRename(parent, realPgDataPath);
            }
            catch
            {
                TryDeleteDirectory(stagingPath);
                throw;
            }

            _logger.LogInformation("Stopping PostgreSQL cluster at '{PgDataPath}'", pgDataPath);
            try
            {
                await RunPgCtlAsync(pgCtl, ["stop", "-D", pgDataPath, "-m", "fast", "-w"], ct);
                _logger.LogInformation("PostgreSQL cluster stopped");
            }
            catch
            {
                TryDeleteDirectory(stagingPath);
                throw;
            }

            try
            {
                Directory.Move(realPgDataPath, oldPath);
                Directory.Move(stagingPath, realPgDataPath);

                _logger.LogInformation(
                    "Starting PostgreSQL cluster at '{PgDataPath}' (server log → '{LogFile}')", pgDataPath, startLog);
                await StartPostgresAsync(pgCtl, pgDataPath, startLog, ct);

                _logger.LogInformation("PostgreSQL cluster started");
                TryDeleteDirectory(oldPath);
            }
            catch (Exception swapException)
            {
                await RecoverClusterAsync(pgCtl, pgDataPath, realPgDataPath, stagingPath, oldPath, failedPath, swapException);
                throw new InvalidOperationException(
                    $"Восстановление не удалось ({swapException.Message}). " +
                    "Кластер возвращён в исходное состояние и запущен.",
                    swapException);
            }
        }
        finally
        {
            TryDeleteFile(startLog);
        }
    }

    private async Task RecoverClusterAsync(
        string pgCtl, string pgDataPath, string realPgDataPath, string stagingPath, string oldPath, string failedPath,
        Exception originalException)
    {
        _logger.LogError(originalException,
            "Restore swap failed at PGDATA '{PgDataPath}' (real path '{RealPath}'). Attempting recovery.",
            pgDataPath, realPgDataPath);

        var pgdataExists = Directory.Exists(realPgDataPath);
        var oldExists = Directory.Exists(oldPath);

        string? rollbackError = null;

        if (pgdataExists && oldExists)
        {
            _logger.LogWarning(
                "Both PGDATA and backup copy exist. Moving new → '{FailedPath}', restoring backup.", failedPath);

            if (!await TryMoveDirectoryWithRetryAsync(pgCtl, realPgDataPath, failedPath))
                rollbackError =
                    $"новый кластер '{realPgDataPath}' не удалось переместить в '{failedPath}'. " +
                    $"Восстановите вручную: переместите '{realPgDataPath}' в безопасное место, затем '{oldPath}' в '{realPgDataPath}'.";
            else if (!await TryMoveDirectoryWithRetryAsync(pgCtl, oldPath, realPgDataPath))
                rollbackError =
                    $"исходный кластер '{oldPath}' не удалось вернуть в '{realPgDataPath}'. " +
                    $"Восстановите вручную: переместите '{oldPath}' в '{realPgDataPath}'.";
        }
        else if (oldExists && !pgdataExists)
        {
            _logger.LogWarning("PGDATA missing, backup at '{OldPath}'. Restoring.", oldPath);
            if (!await TryMoveDirectoryWithRetryAsync(pgCtl, oldPath, realPgDataPath))
                rollbackError =
                    $"исходный кластер '{oldPath}' не удалось вернуть в '{realPgDataPath}'. " +
                    $"Восстановите вручную: переместите '{oldPath}' в '{realPgDataPath}'.";
        }
        else if (pgdataExists && !oldExists)
        {
            _logger.LogInformation("PGDATA at '{RealPath}' intact, no swap occurred.", realPgDataPath);
        }
        else
        {
            rollbackError =
                $"PGDATA '{realPgDataPath}' и резервная копия '{oldPath}' оба отсутствуют. " +
                "Данные могут быть утеряны. Проверьте файловую систему и логи агента.";
        }

        TryDeleteDirectory(stagingPath);

        if (rollbackError != null)
            throw new InvalidOperationException(
                $"Восстановление не удалось завершить автоматически: {rollbackError} Кластер остановлен.",
                originalException);

        var recoveryLog = Path.Combine(Path.GetTempPath(), $"backupster-pg-recovery-{Guid.NewGuid():N}.log");
        try
        {
            _logger.LogInformation("Restarting cluster on original PGDATA at '{PgDataPath}'", pgDataPath);
            await StartPostgresAsync(pgCtl, pgDataPath, recoveryLog, CancellationToken.None);
            _logger.LogInformation("PostgreSQL cluster restarted on original PGDATA after restore failure");
        }
        catch (Exception startException)
        {
            _logger.LogError(startException, "Failed to start cluster after rollback");
            throw new InvalidOperationException(
                $"Восстановление не удалось завершить автоматически: после отката PGDATA '{pgDataPath}' к исходному состоянию " +
                $"кластер не запускается ({startException.Message}). Запустите вручную: pg_ctl start -D \"{pgDataPath}\". " +
                $"Лог запуска: '{recoveryLog}'.",
                originalException);
        }
        finally
        {
            TryDeleteFile(recoveryLog);
        }
    }

    private async Task<bool> TryMoveDirectoryWithRetryAsync(string pgCtl, string from, string to)
    {
        var delays = new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(from))
                {
                    _logger.LogWarning("Source directory '{From}' does not exist; cannot move", from);
                    return false;
                }
                Directory.Move(from, to);
                return true;
            }
            catch (Exception ex) when (attempt < delays.Length)
            {
                _logger.LogWarning(ex,
                    "Failed to move '{From}' → '{To}' (attempt {Attempt}/{Total}). Retrying in {Delay}s.",
                    from, to, attempt + 1, delays.Length + 1, delays[attempt].TotalSeconds);

                await TryStopOrphanedPostmasterAsync(pgCtl, from);
                await Task.Delay(delays[attempt]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to move '{From}' → '{To}' after {Total} attempts", from, to, delays.Length + 1);
                return false;
            }
        }
    }

    private async Task StartPostgresAsync(string pgCtl, string pgDataPath, string startLog, CancellationToken ct)
    {
        var timeoutSeconds = Math.Max(_restoreSettings.PgCtlStartTimeoutSeconds, 1);

        var psi = new ProcessStartInfo
        {
            FileName = pgCtl,
            ArgumentList = { "start", "-D", pgDataPath, "-l", startLog, "-w", "-t", timeoutSeconds.ToString() },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        psi.Environment["LC_MESSAGES"] = "C";
        psi.Environment["LANG"] = "C";

        using var process = new Process { StartInfo = psi };
        process.Start();

        try
        {
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var logContents = TryReadTextFile(startLog);
                throw new InvalidOperationException(
                    $"pg_ctl start завершился с кодом {process.ExitCode} (таймаут: {timeoutSeconds}с). " +
                    $"Лог сервера: {logContents}");
            }
        }
        catch
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill pg_ctl start process"); }
            }
            await TryStopOrphanedPostmasterAsync(pgCtl, pgDataPath);
            throw;
        }
    }

    private async Task TryStopOrphanedPostmasterAsync(string pgCtl, string pgDataPath)
    {
        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await RunPgCtlAsync(pgCtl, ["stop", "-D", pgDataPath, "-m", "immediate", "-w", "-t", "60"], stopCts.Token);
            _logger.LogInformation("Orphaned postmaster at '{PgDataPath}' stopped", pgDataPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to stop orphaned postmaster at '{PgDataPath}' — rollback may fail if postmaster is still holding files",
                pgDataPath);
        }
    }

    private static string TryReadTextFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return "(log file not found)";
            var content = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(content) ? "(empty)" : content;
        }
        catch (Exception ex)
        {
            return $"(failed to read: {ex.Message})";
        }
    }

    private async Task CheckPgCtlAsync(string pgCtl, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pgCtl,
            ArgumentList = { "--version" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.Environment["LC_MESSAGES"] = "C";
        psi.Environment["LANG"] = "C";

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new RestorePermissionException(
                $"pg_ctl не найден на хосте агента ({ex.Message}). " +
                "Установите postgresql и убедитесь, что pg_ctl есть в PATH.");
        }

        if (process.ExitCode != 0)
            throw new RestorePermissionException(
                $"pg_ctl --version вернул код {process.ExitCode}. " +
                "Убедитесь, что postgresql установлен и pg_ctl есть в PATH.");
    }

    private async Task<string> QueryDataDirectoryAsync(ConnectionConfig connection, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(BuildConnectionString(connection));
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand("SHOW data_directory;", conn);
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result is not string path || string.IsNullOrWhiteSpace(path))
            throw new RestorePermissionException(
                $"Не удалось получить путь PGDATA из кластера '{connection.Name}'.");

        return path;
    }

    private async Task RunPgCtlAsync(string pgCtl, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pgCtl,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        psi.Environment["LC_MESSAGES"] = "C";
        psi.Environment["LANG"] = "C";

        using var process = new Process { StartInfo = psi };
        process.Start();

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);

            var stdout = stdoutTask.Result.Trim();
            var stderr = stderrTask.Result.Trim();

            if (stdout.Length > 0)
                _logger.LogDebug("pg_ctl stdout: {Output}", stdout);

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrEmpty(stderr) ? stdout : stderr;
                throw new InvalidOperationException($"pg_ctl завершился с кодом {process.ExitCode}: {detail}");
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill pg_ctl process"); }
            }
            throw;
        }
    }

    private async Task ExtractTarGzAsync(string archivePath, string targetDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "tar",
            ArgumentList = { "-xzf", archivePath, "-C", targetDir },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();
        _logger.LogInformation("tar process started (PID {Pid})", process.Id);

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var stderr = stderrTask.Result.Trim();
                var stdout = stdoutTask.Result.Trim();
                var detail = string.IsNullOrEmpty(stderr) ? stdout : stderr;
                throw new InvalidOperationException($"Распаковка tar завершилась с ошибкой (код {process.ExitCode}): {detail}");
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill tar process"); }
            }
            throw;
        }
    }

    private static (string parent, string leaf) SplitPgDataPath(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        var leaf = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf))
            throw new InvalidOperationException(
                $"Не удалось разобрать путь PGDATA '{path}' на родительский каталог и имя.");
        return (parent, leaf);
    }

    private string ResolveRealPath(string pgDataPath)
    {
        // We resolve only the PGDATA leaf itself. If a parent directory is a symlink, the OS follows it
        // transparently during rename(2)/MoveFile, so no separate handling is needed for parent links.
        var fullPath = Path.GetFullPath(pgDataPath);
        try
        {
            var info = new DirectoryInfo(fullPath);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName is { Length: > 0 } realPath ? realPath : fullPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to resolve symlinks for PGDATA '{PgDataPath}'. Falling back to original path; " +
                "if PGDATA is a symlink to another mount, restore may place data on the wrong volume — " +
                "explicitly point PGDATA at the real path or fix permissions/link integrity to silence this warning.",
                fullPath);
            return fullPath;
        }
    }

    private void EnsureSameFsRename(string parent, string realPgDataPath)
    {
        var probeFromParent = Path.Combine(parent, $"backupster-rename-probe-{Guid.NewGuid():N}");
        var probeToParent = Path.Combine(parent, $"backupster-rename-probe-{Guid.NewGuid():N}");
        var probeInside = Path.Combine(realPgDataPath, $"backupster-rename-probe-{Guid.NewGuid():N}");
        var probeOutside = Path.Combine(parent, $"backupster-rename-probe-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(probeFromParent);
            Directory.Move(probeFromParent, probeToParent);

            Directory.CreateDirectory(probeInside);
            Directory.Move(probeInside, probeOutside);
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(probeFromParent);
            TryDeleteDirectory(probeToParent);
            TryDeleteDirectory(probeInside);
            TryDeleteDirectory(probeOutside);
            throw new InvalidOperationException(
                $"Не удалось выполнить атомарный rename для PGDATA '{realPgDataPath}'. " +
                $"Physical restore требует, чтобы PGDATA и её родительский каталог '{parent}' поддерживали атомарный rename внутри одной FS. " +
                "Не подходят: PGDATA — отдельная точка монтирования Linux (например, '/mnt/db' смонтирован как сама PGDATA); " +
                "Windows volume mount point; cross-FS симлинк, который не удалось разрешить (см. предыдущий warning о ResolveLinkTarget). " +
                $"Подробности: {ex.Message}", ex);
        }
        finally
        {
            TryDeleteDirectory(probeToParent);
            TryDeleteDirectory(probeOutside);
        }
    }

    private static void WriteMarkerFile(string dir)
    {
        var path = Path.Combine(dir, MarkerFileName);
        File.WriteAllText(path, DateTime.UtcNow.ToString("o"));
    }

    private void CleanupOrphanStagingDirs(string parent, string leaf)
    {
        string[] suffixes = ["new", "failed"];
        var threshold = DateTime.UtcNow - TimeSpan.FromHours(OrphanGraceHours);

        foreach (var suffix in suffixes)
        {
            IEnumerable<string> matches;
            try
            {
                matches = Directory.EnumerateDirectories(parent, $"{leaf}.{suffix}-*");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Orphan cleanup: failed to enumerate '{Parent}' for pattern '{Leaf}.{Suffix}-*'",
                    parent, leaf, suffix);
                continue;
            }

            foreach (var dir in matches)
            {
                try
                {
                    var marker = Path.Combine(dir, MarkerFileName);
                    if (!File.Exists(marker))
                    {
                        _logger.LogDebug(
                            "Orphan cleanup: '{Dir}' has no '{Marker}' marker, leaving alone",
                            dir, MarkerFileName);
                        continue;
                    }

                    DateTime createdAt;
                    try
                    {
                        var content = File.ReadAllText(marker).Trim();
                        if (!DateTime.TryParse(content, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out createdAt))
                        {
                            _logger.LogDebug(
                                "Orphan cleanup: '{Dir}' marker has unparseable timestamp '{Content}', leaving alone",
                                dir, content);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "Orphan cleanup: '{Dir}' marker unreadable, leaving alone", dir);
                        continue;
                    }

                    if (createdAt > threshold)
                    {
                        _logger.LogDebug(
                            "Orphan cleanup: '{Dir}' marker created {CreatedAt:o}, younger than {Hours}h, leaving alone",
                            dir, createdAt, OrphanGraceHours);
                        continue;
                    }

                    _logger.LogWarning(
                        "Orphan cleanup: deleting stale staging dir '{Dir}' (marker created {CreatedAt:o}, age > {Hours}h)",
                        dir, createdAt, OrphanGraceHours);
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Orphan cleanup: failed to process '{Dir}'", dir);
                }
            }
        }
    }

    private async Task VerifyStagedClusterAsync(string stagingPath, string pgCtl, CancellationToken ct)
    {
        var versionFile = Path.Combine(stagingPath, "PG_VERSION");
        if (!File.Exists(versionFile))
            throw new InvalidOperationException(
                $"Распакованный архив в '{stagingPath}' не содержит PG_VERSION — " +
                "это не похоже на PGDATA от pg_basebackup. Возможно, архив повреждён.");

        if (!Directory.Exists(Path.Combine(stagingPath, "global")))
            throw new InvalidOperationException(
                $"Распакованный архив в '{stagingPath}' не содержит каталога 'global'. " +
                "Возможно, архив повреждён.");

        var pgTblspc = Path.Combine(stagingPath, "pg_tblspc");
        if (Directory.Exists(pgTblspc))
        {
            var entries = Directory.GetFileSystemEntries(pgTblspc);
            if (entries.Length > 0)
            {
                var oids = string.Join(", ", entries.Select(Path.GetFileName));
                throw new InvalidOperationException(
                    $"Бэкап содержит tablespaces (pg_tblspc/{{{oids}}}), но physical-режим Backupster их не поддерживает: " +
                    "данные tablespace не шиппятся в архиве, после restore остались бы битые симлинки. " +
                    "Бэкап создан старой версией агента до guard'а — пересоздайте его в logical-режиме либо удалите tablespaces в источнике.");
            }
        }

        var archiveMajor = (await File.ReadAllTextAsync(versionFile, ct)).Trim();
        var pgCtlMajor = await GetPgCtlMajorVersionAsync(pgCtl, ct);

        if (!string.Equals(archiveMajor, pgCtlMajor, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Мажорная версия бэкапа (PG_VERSION={archiveMajor}) не совпадает с версией pg_ctl ({pgCtlMajor}). " +
                "Восстановите бэкап на PostgreSQL той же мажорной версии.");
    }

    private async Task<string> GetPgCtlMajorVersionAsync(string pgCtl, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pgCtl,
            ArgumentList = { "--version" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["LC_MESSAGES"] = "C";
        psi.Environment["LANG"] = "C";

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var match = Regex.Match(stdout, @"(\d+)(?:\.\d+)?\s*$");
        if (!match.Success)
            throw new InvalidOperationException(
                $"Не удалось определить мажорную версию pg_ctl. Вывод '--version': '{stdout.Trim()}'.");
        return match.Groups[1].Value;
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete directory '{Path}'", path);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete file '{Path}'", path);
        }
    }

    private static string BuildConnectionString(ConnectionConfig connection) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = connection.Host,
            Port = connection.Port,
            Username = connection.Username,
            Password = connection.Password,
            Database = "postgres",
        }.ToString();

    private async Task EnsureClusterIsNotServiceManagedAsync(string pgDataPath, CancellationToken ct)
    {
        var pidFile = Path.Combine(pgDataPath, "postmaster.pid");
        if (!File.Exists(pidFile))
        {
            _logger.LogWarning(
                "postmaster.pid not found in '{PgDataPath}' — skipping service-manager detection", pgDataPath);
            return;
        }

        int postmasterPid;
        try
        {
            var firstLine = (await File.ReadAllLinesAsync(pidFile, ct)).FirstOrDefault();
            if (!int.TryParse(firstLine?.Trim(), out postmasterPid))
            {
                _logger.LogWarning(
                    "Failed to parse postmaster PID from '{PidFile}' (first line: '{Line}') — skipping service-manager detection",
                    pidFile, firstLine);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read '{PidFile}' — skipping service-manager detection", pidFile);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            var unit = await DetectSystemdUnitAsync(postmasterPid, ct);
            if (unit is null) return;

            throw new RestorePermissionException(
                $"PostgreSQL управляется systemd-юнитом '{unit}'. " +
                "Physical restore работает только с unmanaged-кластером, иначе systemd может перезапустить " +
                "postmaster во время swap, а после restore статус юнита разойдётся с реальным состоянием. " +
                "Остановите сервис и отключите автозапуск:\n" +
                $"    sudo systemctl stop {unit}\n" +
                $"    sudo systemctl disable {unit}\n" +
                "После успешного restore верните автозапуск (systemctl enable) и стартуйте кластер через сервис.");
        }
        else if (OperatingSystem.IsWindows())
        {
            var service = await DetectWindowsServiceAsync(postmasterPid, ct);
            if (service is null) return;

            throw new RestorePermissionException(
                $"PostgreSQL управляется Windows-сервисом '{service}'. " +
                "Physical restore работает только с unmanaged-кластером, иначе Service Control Manager может " +
                "перезапустить postmaster во время swap, а после restore статус сервиса разойдётся с реальным состоянием. " +
                "Остановите сервис и переведите автозапуск в Manual:\n" +
                $"    Stop-Service '{service}'\n" +
                $"    Set-Service '{service}' -StartupType Manual\n" +
                "После успешного restore верните автозапуск и стартуйте кластер через сервис.");
        }
    }

    private async Task<string?> DetectSystemdUnitAsync(int pid, CancellationToken ct)
    {
        var cgroupFile = $"/proc/{pid}/cgroup";
        if (!File.Exists(cgroupFile)) return null;

        string content;
        try
        {
            content = await File.ReadAllTextAsync(cgroupFile, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read '{CgroupFile}' — skipping systemd detection", cgroupFile);
            return null;
        }

        var match = Regex.Match(content, @"system\.slice/([^\s/]+\.service)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private async Task<string?> DetectWindowsServiceAsync(int pid, CancellationToken ct)
    {
        var script =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
            $"Get-CimInstance Win32_Service -Filter 'ProcessId={pid}' | " +
            "Select-Object -First 1 -ExpandProperty Name";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to spawn powershell — skipping Windows service detection");
            return null;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0) return null;

        var name = stdout.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
