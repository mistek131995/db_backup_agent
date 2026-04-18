using DbBackupAgent.Configuration;
using Microsoft.Extensions.Options;

namespace DbBackupAgent.Services;

public sealed class ConnectionResolver
{
    private readonly Dictionary<string, ConnectionConfig> _byName;

    public ConnectionResolver(IOptions<List<ConnectionConfig>> connections)
        : this(connections.Value)
    {
    }

    public ConnectionResolver(IEnumerable<ConnectionConfig> connections)
    {
        _byName = new Dictionary<string, ConnectionConfig>(StringComparer.Ordinal);

        foreach (var conn in connections)
        {
            if (string.IsNullOrWhiteSpace(conn.Name))
                throw new InvalidOperationException("Connection name cannot be empty.");

            if (!_byName.TryAdd(conn.Name, conn))
                throw new InvalidOperationException(
                    $"Duplicate connection name '{conn.Name}'. Connection names must be unique.");
        }
    }

    public IReadOnlyCollection<string> Names => _byName.Keys;

    public bool TryResolve(string name, out ConnectionConfig connection) =>
        _byName.TryGetValue(name, out connection!);

    public ConnectionConfig Resolve(string name)
    {
        if (!_byName.TryGetValue(name, out var conn))
            throw new InvalidOperationException(
                $"Connection '{name}' not found. Available connections: " +
                (_byName.Count == 0 ? "(none)" : string.Join(", ", _byName.Keys)));

        return conn;
    }
}
