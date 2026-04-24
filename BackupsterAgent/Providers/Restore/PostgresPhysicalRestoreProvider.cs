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

        await EnsureClusterIsNotServiceManagedAsync(_pgDataPath, ct);
    }

    public Task PrepareTargetDatabaseAsync(ConnectionConfig connection, string targetDatabase, bool replaceExisting, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public async Task RestoreAsync(ConnectionConfig connection, string targetDatabase, string restoreFilePath, CancellationToken ct)
    {
        var pgDataPath = _pgDataPath!;
        var pgCtl = _pgCtlPath ?? await _binaryResolver.ResolveAsync(connection, "pg_ctl", ct);

        var (parent, leaf) = SplitPgDataPath(pgDataPath);
        var guid = Guid.NewGuid().ToString("N")[..8];
        var stagingPath = Path.Combine(parent, $"{leaf}.new-{guid}");
        var oldPath = Path.Combine(parent, $"{leaf}.old-{guid}");
        var failedPath = Path.Combine(parent, $"{leaf}.failed-{guid}");

        Directory.CreateDirectory(stagingPath);
        try
        {
            _logger.LogInformation(
                "Extracting base archive '{ArchivePath}' to staging '{StagingPath}'", restoreFilePath, stagingPath);
            await ExtractTarGzAsync(restoreFilePath, stagingPath, ct);
            await VerifyStagedClusterAsync(stagingPath, pgCtl, ct);
            _logger.LogInformation("Staged cluster verified at '{StagingPath}'", stagingPath);
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
            Directory.Move(pgDataPath, oldPath);
        }
        catch
        {
            TryDeleteDirectory(stagingPath);
            throw;
        }

        try
        {
            Directory.Move(stagingPath, pgDataPath);
        }
        catch
        {
            TryMoveDirectory(oldPath, pgDataPath);
            TryDeleteDirectory(stagingPath);
            throw;
        }

        var startLog = Path.Combine(Path.GetTempPath(), $"backupster-pg-start-{Guid.NewGuid():N}.log");
        _logger.LogInformation(
            "Starting PostgreSQL cluster at '{PgDataPath}' (server log → '{LogFile}')", pgDataPath, startLog);
        try
        {
            await StartPostgresAsync(pgCtl, pgDataPath, startLog, ct);
        }
        catch
        {
            _logger.LogError(
                "Cluster failed to start after swap. Reverting: new PGDATA → '{FailedPath}', '{OldPath}' → PGDATA",
                failedPath, oldPath);
            TryMoveDirectory(pgDataPath, failedPath);
            TryMoveDirectory(oldPath, pgDataPath);
            throw;
        }

        _logger.LogInformation("PostgreSQL cluster started");
        TryDeleteDirectory(oldPath);
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
                    $"pg_ctl start exited with code {process.ExitCode} (timeout: {timeoutSeconds}s). " +
                    $"Server log: {logContents}");
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
                throw new InvalidOperationException($"pg_ctl exited with code {process.ExitCode}: {detail}");
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
                throw new InvalidOperationException($"tar extraction failed (exit code {process.ExitCode}): {detail}");
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

    private void TryMoveDirectory(string from, string to)
    {
        try
        {
            if (Directory.Exists(from))
                Directory.Move(from, to);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move directory '{From}' → '{To}'", from, to);
        }
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
