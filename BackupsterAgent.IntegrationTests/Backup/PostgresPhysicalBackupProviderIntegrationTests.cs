using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using BackupsterAgent.Configuration;
using BackupsterAgent.Providers.Backup;
using BackupsterAgent.Services.Common.Processes;
using BackupsterAgent.Services.Common.Resolvers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BackupsterAgent.IntegrationTests.Backup;

[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class PostgresPhysicalBackupProviderIntegrationTests
{
    private const string TestDbPrefix = "bp_itest_pg_phys_";

    private ConnectionConfig _connection = null!;
    private ExternalProcessRunner _runner = null!;
    private PostgresBinaryResolver _resolver = null!;

    private string _srcDb = null!;
    private string _outputDir = null!;
    private string _restoreDir = null!;
    private string _serverLogPath = null!;
    private string _pgCtlBinary = null!;
    private int _restorePort;
    private bool _serverStarted;
    private CancellationTokenSource _cts = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        Assume.That(
            IntegrationConfig.TryGetPostgresConnection(out var connection),
            Is.True,
            "Postgres:* not configured; set via dotnet user-secrets or BACKUPSTER_INTEGRATION_POSTGRES__* env vars.");

        _connection = connection;
        _runner = new ExternalProcessRunner(NullLogger<ExternalProcessRunner>.Instance);
        _resolver = new PostgresBinaryResolver(NullLogger<PostgresBinaryResolver>.Instance);

        using var bootCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await DropLeftoverTestDatabasesAsync(_connection, bootCts.Token);
    }

    [SetUp]
    public async Task SetUp()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        _srcDb = TestDbPrefix + "src_" + suffix;

        _outputDir = Path.Combine(Path.GetTempPath(), "backupster-pg-phys-out-" + Guid.NewGuid().ToString("N"));
        _restoreDir = Path.Combine(Path.GetTempPath(), "backupster-pg-phys-pgdata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outputDir);

        _serverLogPath = Path.Combine(_outputDir, "test-server.log");
        _serverStarted = false;

        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        _pgCtlBinary = await _resolver.ResolveAsync(_connection, "pg_ctl", _cts.Token);
        await CreateSourceDatabaseAsync(_connection, _srcDb, _cts.Token);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serverStarted)
        {
            await TryStopRestoreServerAsync();
            _serverStarted = false;
        }

        try
        {
            await DropDatabaseIfExistsAsync(_connection, _srcDb, CancellationToken.None);
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"Source DB cleanup failed: {ex.Message}");
        }

        _cts?.Dispose();

        TryDeleteDirectory(_restoreDir, "restore dir");
        TryDeleteDirectory(_outputDir, "output dir");
    }

    [Test]
    public async Task BackupAsync_RoundTrip_RestoredInstanceHasContent()
    {
        var provider = new PostgresPhysicalBackupProvider(
            NullLogger<PostgresPhysicalBackupProvider>.Instance,
            _resolver,
            _runner);

        var config = new DatabaseConfig
        {
            ConnectionName = _connection.Name,
            StorageName = "n/a",
            Database = _srcDb,
            OutputPath = _outputDir,
        };

        var result = await provider.BackupAsync(config, _connection, _cts.Token);

        Assert.That(result.Success, Is.True);
        Assert.That(File.Exists(result.FilePath), Is.True, "tar.gz file must exist after backup");
        Assert.That(result.SizeBytes, Is.GreaterThan(0));

        await ExtractTarGzAsync(result.FilePath, _restoreDir, _cts.Token);

        Assert.That(File.Exists(Path.Combine(_restoreDir, "PG_VERSION")), Is.True,
            "extracted archive must contain PG_VERSION marker");

        await StartRestoreServerWithPortRetryAsync(maxAttempts: 3);

        await WaitForServerReadyAsync(_restorePort, _connection, _cts.Token);

        var rows = await ReadItemsAsync(_connection, _restorePort, _srcDb, _cts.Token);

        Assert.That(rows, Is.EquivalentTo(ExpectedRows));
    }

    // Rows inserted before CHECKPOINT — expected to be present in datafiles inside the basebackup.
    private static readonly (int Id, string Name)[] PreCheckpointRows =
    [
        (1, "alpha"),
        (2, "beta"),
        (3, "gamma"),
    ];

    // Rows inserted after CHECKPOINT without an explicit second CHECKPOINT — exercise WAL replay on restore.
    private static readonly (int Id, string Name)[] PostCheckpointRows =
    [
        (4, "delta"),
        (5, "epsilon"),
        (6, "zeta"),
    ];

    private static readonly (int Id, string Name)[] ExpectedRows =
        PreCheckpointRows.Concat(PostCheckpointRows).ToArray();

    private async Task StartRestoreServerWithPortRetryAsync(int maxAttempts)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            _restorePort = FindFreeLoopbackPort();
            AppendPostgresConfOverrides(_restoreDir, _restorePort);

            var exitCode = await RunPgCtlDirectAsync(
                new[]
                {
                    "-D", _restoreDir,
                    "-l", _serverLogPath,
                    "-w",
                    "-t", "120",
                    "start",
                },
                timeout: TimeSpan.FromSeconds(150),
                _cts.Token);

            if (exitCode == 0)
            {
                _serverStarted = true;
                return;
            }

            var tail = TryReadServerLogTail(_serverLogPath, 60);
            if (attempt < maxAttempts && LooksLikePortBindFailure(tail))
            {
                TestContext.Progress.WriteLine(
                    $"pg_ctl start (attempt {attempt}) failed on port {_restorePort} due to bind contention; retrying with a fresh port.");
                continue;
            }

            throw new InvalidOperationException(
                $"pg_ctl start failed with exit {exitCode} on attempt {attempt}/{maxAttempts}. Server log tail:{Environment.NewLine}{tail}");
        }
    }

    private static bool LooksLikePortBindFailure(string serverLogTail) =>
        serverLogTail.Contains("could not bind", StringComparison.OrdinalIgnoreCase)
        || serverLogTail.Contains("Address already in use", StringComparison.OrdinalIgnoreCase)
        || serverLogTail.Contains("could not create any TCP/IP sockets", StringComparison.OrdinalIgnoreCase);

    private async Task TryStopRestoreServerAsync()
    {
        try
        {
            var fast = await RunPgCtlStopDirectAsync("fast", timeoutSeconds: 10);
            if (fast == 0) return;

            TestContext.Progress.WriteLine($"pg_ctl stop -m fast failed with exit {fast}; retrying immediate.");
            var immediate = await RunPgCtlStopDirectAsync("immediate", timeoutSeconds: 10);
            if (immediate != 0)
            {
                TestContext.Progress.WriteLine(
                    $"pg_ctl stop -m immediate failed with exit {immediate}; trying PID kill.");
                TryKillByPidFile(_restoreDir);
            }
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"Stop attempt threw: {ex.Message}; trying PID kill.");
            TryKillByPidFile(_restoreDir);
        }
    }

    private async Task<int> RunPgCtlStopDirectAsync(string mode, int timeoutSeconds)
    {
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds + 10));
        return await RunPgCtlDirectAsync(
            new[]
            {
                "-D", _restoreDir,
                "-m", mode,
                "-w",
                "-t", timeoutSeconds.ToString(),
                "stop",
            },
            timeout: TimeSpan.FromSeconds(timeoutSeconds + 10),
            stopCts.Token);
    }

    private async Task<int> RunPgCtlDirectAsync(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _pgCtlBinary,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LC_MESSAGES"] = "C";
        psi.Environment["LANG"] = "C";

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        process.Start();

        using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct);
        combined.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(combined.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
            throw;
        }

        return process.ExitCode;
    }

    private static void TryKillByPidFile(string pgData)
    {
        try
        {
            var pidFile = Path.Combine(pgData, "postmaster.pid");
            if (!File.Exists(pidFile)) return;
            var firstLine = File.ReadLines(pidFile).FirstOrDefault();
            if (!int.TryParse(firstLine, out var pid)) return;
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { /* already gone */ }
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"PID kill failed: {ex.Message}");
        }
    }

    private static async Task ExtractTarGzAsync(string archivePath, string targetDir, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);
        await using var fileStream = File.OpenRead(archivePath);
        await using var gz = new GZipStream(fileStream, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gz, targetDir, overwriteFiles: true, ct);
    }

    private static int FindFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void AppendPostgresConfOverrides(string pgData, int port)
    {
        var confPath = Path.Combine(pgData, "postgresql.conf");
        var overrides = string.Join(Environment.NewLine,
        [
            string.Empty,
            "# overrides appended by integration test",
            $"port = {port}",
            "listen_addresses = 'localhost'",
            "unix_socket_directories = ''",
            "ssl = off",
            string.Empty,
        ]);
        File.AppendAllText(confPath, overrides);
    }

    private static async Task WaitForServerReadyAsync(int port, ConnectionConfig connection, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var connString = new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = port,
            Username = connection.Username,
            Password = connection.Password,
            Database = "postgres",
            Timeout = 2,
        }.ToString();

        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var conn = new NpgsqlConnection(connString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
                await cmd.ExecuteScalarAsync(ct);
                return;
            }
            catch (Exception ex) when (IsTransientStartupError(ex))
            {
                last = ex;
                await Task.Delay(500, ct);
            }
        }

        throw new TimeoutException(
            $"Restored server on port {port} did not become ready within 30 seconds. Last error: {last?.Message}");
    }

    private static bool IsTransientStartupError(Exception ex)
    {
        switch (ex)
        {
            case SocketException:
            case TimeoutException:
                return true;
            case PostgresException pg:
                // 57P03 — cannot_connect_now (database is starting up / in recovery)
                return pg.SqlState == "57P03";
            case NpgsqlException npg when npg.InnerException is SocketException:
                return true;
            default:
                return false;
        }
    }

    private static async Task CreateSourceDatabaseAsync(ConnectionConfig connection, string dbName, CancellationToken ct)
    {
        await ExecuteOnDatabaseAsync(connection, "postgres", $"CREATE DATABASE \"{dbName}\";", ct);

        var ddl = "CREATE TABLE items (id INT PRIMARY KEY, name TEXT NOT NULL);";
        await ExecuteOnDatabaseAsync(connection, dbName, ddl, ct);

        await InsertRowsAsync(connection, dbName, PreCheckpointRows, ct);
        await ExecuteOnDatabaseAsync(connection, dbName, "CHECKPOINT;", ct);

        // Post-checkpoint inserts deliberately stay in WAL only — they validate that
        // pg_basebackup captured enough WAL for the restored cluster to replay them on startup.
        await InsertRowsAsync(connection, dbName, PostCheckpointRows, ct);
    }

    private static async Task InsertRowsAsync(
        ConnectionConfig connection, string dbName, (int Id, string Name)[] rows, CancellationToken ct)
    {
        foreach (var (id, name) in rows)
        {
            await using var conn = new NpgsqlConnection(BuildConnectionString(connection, dbName));
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("INSERT INTO items (id, name) VALUES (@id, @name);", conn);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("name", name);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<List<(int Id, string Name)>> ReadItemsAsync(
        ConnectionConfig connection, int port, string dbName, CancellationToken ct)
    {
        var connString = new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = port,
            Username = connection.Username,
            Password = connection.Password,
            Database = dbName,
            Timeout = 5,
        }.ToString();

        var rows = new List<(int, string)>();
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id, name FROM items;", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        return rows;
    }

    private static async Task DropDatabaseIfExistsAsync(ConnectionConfig connection, string dbName, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(BuildConnectionString(connection, "postgres"));
        await conn.OpenAsync(ct);

        await using (var terminate = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "WHERE datname = @db AND pid <> pg_backend_pid();", conn))
        {
            terminate.Parameters.AddWithValue("db", dbName);
            await terminate.ExecuteNonQueryAsync(ct);
        }

        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\";", conn);
        await drop.ExecuteNonQueryAsync(ct);
    }

    private static async Task DropLeftoverTestDatabasesAsync(ConnectionConfig connection, CancellationToken ct)
    {
        var leftovers = new List<string>();
        await using (var conn = new NpgsqlConnection(BuildConnectionString(connection, "postgres")))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT datname FROM pg_database WHERE datname LIKE @prefix;", conn);
            cmd.Parameters.AddWithValue("prefix", TestDbPrefix + "%");
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                leftovers.Add(reader.GetString(0));
        }

        foreach (var name in leftovers)
        {
            try { await DropDatabaseIfExistsAsync(connection, name, ct); }
            catch (Exception ex)
            {
                TestContext.Progress.WriteLine($"Leftover DB '{name}' cleanup failed: {ex.Message}");
            }
        }
    }

    private static async Task ExecuteOnDatabaseAsync(
        ConnectionConfig connection, string dbName, string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(BuildConnectionString(connection, dbName));
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string BuildConnectionString(ConnectionConfig connection, string database) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = connection.Host,
            Port = connection.Port,
            Username = connection.Username,
            Password = connection.Password,
            Database = database,
        }.ToString();

    private static string TryReadServerLogTail(string path, int lineCount)
    {
        try
        {
            if (!File.Exists(path)) return "(server log not found)";
            var lines = File.ReadAllLines(path);
            var tail = lines.Length <= lineCount ? lines : lines[^lineCount..];
            return string.Join(Environment.NewLine, tail);
        }
        catch (Exception ex)
        {
            return $"(failed to read server log: {ex.Message})";
        }
    }

    private static void TryDeleteDirectory(string path, string description)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"{description} cleanup failed for '{path}': {ex.Message}");
        }
    }
}
