using BackupsterAgent.Configuration;

namespace BackupsterAgent.Services.Common;

public sealed class StorageResolver
{
    private readonly Dictionary<string, StorageConfig> _byName;

    public StorageResolver(IEnumerable<StorageConfig> storages)
    {
        _byName = new Dictionary<string, StorageConfig>(StringComparer.Ordinal);

        foreach (var storage in storages)
        {
            if (string.IsNullOrWhiteSpace(storage.Name))
                throw new InvalidOperationException("Storage name cannot be empty.");

            if (!_byName.TryAdd(storage.Name, storage))
                throw new InvalidOperationException(
                    $"Duplicate storage name '{storage.Name}'. Storage names must be unique.");
        }
    }

    public IReadOnlyCollection<string> Names => _byName.Keys;

    public bool TryResolve(string name, out StorageConfig storage) =>
        _byName.TryGetValue(name, out storage!);

    public StorageConfig Resolve(string name)
    {
        if (!_byName.TryGetValue(name, out var storage))
            throw new InvalidOperationException(
                $"Storage '{name}' not found. Available storages: " +
                (_byName.Count == 0 ? "(none)" : string.Join(", ", _byName.Keys)));

        return storage;
    }
}
