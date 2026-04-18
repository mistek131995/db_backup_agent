using DbBackupAgent.Configuration;

namespace DbBackupAgent.Services;

public static class MssqlSharedPathResolver
{
    private static readonly HashSet<string> LocalHostAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "::1", "0.0.0.0",
    };

    public static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (LocalHostAliases.Contains(host)) return true;
        return host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    public static string JoinSqlPath(string dir, string fileName)
    {
        var trimmed = dir.TrimEnd('/', '\\');
        var usesBackslash = trimmed.Contains('\\')
            || (trimmed.Length >= 2 && trimmed[1] == ':');
        var separator = usesBackslash ? "\\" : "/";
        return trimmed + separator + fileName;
    }

    public static string GetSqlDir(ConnectionConfig connection, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(connection.SharedBackupPath))
            return connection.SharedBackupPath!;

        if (!IsLocalHost(connection.Host))
            throw new InvalidOperationException(BuildRemoteNoSharedMessage(connection));

        return fallback;
    }

    public static string GetAgentDir(ConnectionConfig connection, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(connection.AgentBackupPath))
            return connection.AgentBackupPath!;

        if (!string.IsNullOrWhiteSpace(connection.SharedBackupPath))
            return connection.SharedBackupPath!;

        if (!IsLocalHost(connection.Host))
            throw new InvalidOperationException(BuildRemoteNoSharedMessage(connection));

        return fallback;
    }

    private static string BuildRemoteNoSharedMessage(ConnectionConfig connection) =>
        $"Remote MSSQL ('{connection.Host}', подключение '{connection.Name}') требует SharedBackupPath " +
        "в ConnectionConfig — путь к каталогу .bak, видимый и агенту, и SQL Server. Подробнее — в README агента.";
}
