using System.Collections.Concurrent;
using BackupsterAgent.Configuration;
using Npgsql;

namespace BackupsterAgent.Services.Common.Resolvers;

public sealed class PostgresBinaryResolver
{
    private readonly ILogger<PostgresBinaryResolver> _logger;
    private readonly ConcurrentDictionary<string, string?> _binDirCache = new();

    public PostgresBinaryResolver(ILogger<PostgresBinaryResolver> logger)
    {
        _logger = logger;
    }

    public async Task<string> ResolveAsync(ConnectionConfig connection, string binaryName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(binaryName))
            throw new ArgumentException("Binary name is required", nameof(binaryName));

        var key = BuildCacheKey(connection);
        if (_binDirCache.TryGetValue(key, out var cachedDir))
            return ComposePath(cachedDir, binaryName);

        var dir = await ResolveDirectoryAsync(connection, ct);
        _binDirCache[key] = dir;
        return ComposePath(dir, binaryName);
    }

    private async Task<string?> ResolveDirectoryAsync(ConnectionConfig connection, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(connection.PostgresBinPath))
        {
            var overridePath = connection.PostgresBinPath!;
            if (!Directory.Exists(overridePath))
                throw new InvalidOperationException(
                    $"Configured PostgresBinPath '{overridePath}' for connection '{connection.Name}' does not exist.");

            _logger.LogInformation(
                "Using configured PostgresBinPath '{Dir}' for connection '{Name}'",
                overridePath, connection.Name);
            return overridePath;
        }

        int? major;
        try
        {
            major = await QueryServerMajorAsync(connection, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to query server_version_num for connection '{Name}', falling back to PATH for binary resolution",
                connection.Name);
            return null;
        }

        if (major is null)
        {
            _logger.LogWarning(
                "server_version_num returned no value for connection '{Name}', falling back to PATH",
                connection.Name);
            return null;
        }

        var candidate = FindBinDirForMajor(major.Value);
        if (candidate is not null)
        {
            _logger.LogInformation(
                "Resolved PostgreSQL {Major} bin directory for connection '{Name}': '{Dir}'",
                major, connection.Name, candidate);
            return candidate;
        }

        _logger.LogInformation(
            "No matching PostgreSQL {Major} install found on agent host for connection '{Name}', falling back to PATH",
            major, connection.Name);
        return null;
    }

    private static async Task<int?> QueryServerMajorAsync(ConnectionConfig connection, CancellationToken ct)
    {
        var connString = new NpgsqlConnectionStringBuilder
        {
            Host = connection.Host,
            Port = connection.Port,
            Username = connection.Username,
            Password = connection.Password,
            Database = "postgres",
        }.ToString();

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand("SHOW server_version_num;", conn);
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result is string s && int.TryParse(s, out var versionNum) && versionNum > 0)
            return versionNum / 10000;

        return null;
    }

    private static string? FindBinDirForMajor(int major)
    {
        if (OperatingSystem.IsWindows())
        {
            var regBase = TryReadWindowsInstallBase(major);
            if (regBase is not null)
            {
                var bin = Path.Combine(regBase, "bin");
                if (Directory.Exists(bin)) return bin;
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var conv = Path.Combine(programFiles, "PostgreSQL", major.ToString(), "bin");
            if (Directory.Exists(conv)) return conv;

            return null;
        }

        var deb = $"/usr/lib/postgresql/{major}/bin";
        if (Directory.Exists(deb)) return deb;

        var rhel = $"/usr/pgsql-{major}/bin";
        if (Directory.Exists(rhel)) return rhel;

        return null;
    }

    private static string? TryReadWindowsInstallBase(int major)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\PostgreSQL\Installations\postgresql-x64-{major}");
            return key?.GetValue("Base Directory") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string ComposePath(string? dir, string binaryName)
    {
        if (dir is null) return binaryName;

        var withExt = OperatingSystem.IsWindows() && !binaryName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? binaryName + ".exe"
            : binaryName;

        return Path.Combine(dir, withExt);
    }

    private static string BuildCacheKey(ConnectionConfig c) =>
        $"{c.PostgresBinPath ?? string.Empty}|{c.Host}:{c.Port}";
}
