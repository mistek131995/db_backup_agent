using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BackupsterAgent.Services.Common;

public sealed class RunStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly string _rootDir;
    private readonly ILogger<RunStateStore> _logger;
    private readonly object _writeLock = new();

    public RunStateStore(string rootDir, ILogger<RunStateStore> logger)
    {
        _rootDir = rootDir;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, DateTime> LoadAll()
    {
        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        if (!Directory.Exists(_rootDir))
            return result;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_rootDir, "*.json", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex,
                "RunStateStore: failed to enumerate '{Path}' — starting with empty state", _rootDir);
            return result;
        }

        foreach (var path in files)
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var text = File.ReadAllText(path);
                var entry = JsonSerializer.Deserialize<RunStateEntry>(text, JsonOptions);
                if (entry is null || string.IsNullOrWhiteSpace(entry.DatabaseName))
                {
                    _logger.LogWarning(
                        "RunStateStore: file '{Path}' missing databaseName — skipping", path);
                    continue;
                }

                if (result.TryGetValue(entry.DatabaseName, out var existing) && existing >= entry.LastRunUtc)
                    continue;

                result[entry.DatabaseName] = entry.LastRunUtc;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex,
                    "RunStateStore: failed to read '{Path}' — skipping", path);
            }
        }

        return result;
    }

    public void Write(string databaseName, DateTime lastRunUtc)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("databaseName must not be empty", nameof(databaseName));

        lock (_writeLock)
        {
            Directory.CreateDirectory(_rootDir);

            var fileName = GetFileName(databaseName);
            var finalPath = Path.Combine(_rootDir, fileName);
            var tempPath = Path.Combine(_rootDir, $"{fileName}.tmp-{Guid.NewGuid():N}");

            var entry = new RunStateEntry
            {
                DatabaseName = databaseName,
                LastRunUtc = lastRunUtc,
            };

            try
            {
                using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, entry, JsonOptions);
                    fs.Flush(true);
                }

                File.Move(tempPath, finalPath, overwrite: true);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex,
                    "RunStateStore: failed to persist last-run for '{Database}'. In-memory state is still updated.",
                    databaseName);
                TryDelete(tempPath);
            }
        }
    }

    private static string GetFileName(string databaseName)
    {
        var sanitized = Sanitize(databaseName);
        var hash = HashSuffix(databaseName);
        return $"{sanitized}_{hash}.json";
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, 60));
        foreach (var c in s)
        {
            if (sb.Length >= 60) break;
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.Length == 0 ? "db" : sb.ToString();
    }

    private static string HashSuffix(string s)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(s), hash);
        return Convert.ToHexString(hash[..4]);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
