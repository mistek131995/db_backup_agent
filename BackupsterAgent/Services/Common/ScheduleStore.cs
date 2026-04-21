using System.Text.Json;
using BackupsterAgent.Contracts;

namespace BackupsterAgent.Services.Common;

public sealed class ScheduleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly string _filePath;
    private readonly ILogger<ScheduleStore> _logger;
    private readonly object _writeLock = new();

    public ScheduleStore(string filePath, ILogger<ScheduleStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public ScheduleDto? TryLoad()
    {
        if (!File.Exists(_filePath))
            return null;

        try
        {
            var text = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<ScheduleDto>(text, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex,
                "ScheduleStore: failed to read cached schedule from '{Path}'", _filePath);
            return null;
        }
    }

    public void Write(ScheduleDto schedule)
    {
        lock (_writeLock)
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);

            var tempPath = $"{_filePath}.tmp-{Guid.NewGuid():N}";
            try
            {
                using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, schedule, JsonOptions);
                    fs.Flush(true);
                }

                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex,
                    "ScheduleStore: failed to persist schedule to '{Path}'", _filePath);
                TryDelete(tempPath);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
