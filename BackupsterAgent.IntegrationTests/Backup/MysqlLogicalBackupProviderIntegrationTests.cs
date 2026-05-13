using System.IO.Compression;
using BackupsterAgent.Configuration;
using BackupsterAgent.Providers.Backup;
using BackupsterAgent.Providers.Restore;
using BackupsterAgent.Services.Common.Processes;
using BackupsterAgent.Services.Common.Resolvers;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace BackupsterAgent.IntegrationTests.Backup;

[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class MysqlLogicalBackupProviderIntegrationTests
{
    private const string TestDbPrefix = "bp_itest_mysql_log_";

    private ConnectionConfig _connection = null!;
    private ExternalProcessRunner _runner = null!;
    private MysqlBinaryResolver _resolver = null!;

    private string _srcDb = null!;
    private string _dstDb = null!;
    private string _outputDir = null!;
    private DateTime _testStartUtc;
    private CancellationTokenSource _cts = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        Assume.That(
            IntegrationConfig.TryGetMysqlConnection(out var connection),
            Is.True,
            "Mysql:* not configured; set via dotnet user-secrets or BACKUPSTER_INTEGRATION_MYSQL__* env vars.");

        _connection = connection;
        _runner = new ExternalProcessRunner(NullLogger<ExternalProcessRunner>.Instance);
        _resolver = new MysqlBinaryResolver(NullLogger<MysqlBinaryResolver>.Instance);

        using var bootCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await DropLeftoverTestDatabasesAsync(_connection, bootCts.Token);
    }

    [SetUp]
    public async Task SetUp()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        _srcDb = TestDbPrefix + "src_" + suffix;
        _dstDb = TestDbPrefix + "dst_" + suffix;

        _outputDir = Path.Combine(Path.GetTempPath(), "backupster-mysql-log-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outputDir);

        _testStartUtc = DateTime.UtcNow;
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        await CreateSourceDatabaseAsync(_connection, _srcDb, _cts.Token);
    }

    [TearDown]
    public async Task TearDown()
    {
        try
        {
            await DropDatabaseIfExistsAsync(_connection, _srcDb, CancellationToken.None);
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"Source DB cleanup failed: {ex.Message}");
        }

        try
        {
            await DropDatabaseIfExistsAsync(_connection, _dstDb, CancellationToken.None);
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"Target DB cleanup failed: {ex.Message}");
        }

        _cts?.Dispose();

        try
        {
            if (Directory.Exists(_outputDir))
                Directory.Delete(_outputDir, recursive: true);
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"Local output cleanup failed: {ex.Message}");
        }
    }

    [Test]
    public async Task BackupAsync_RoundTrip_RestoredDatabaseHasContent()
    {
        var provider = new MysqlLogicalBackupProvider(
            NullLogger<MysqlLogicalBackupProvider>.Instance,
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
        Assert.That(File.Exists(result.FilePath), Is.True, "sql.gz file must exist after backup");
        Assert.That(result.SizeBytes, Is.GreaterThan(0));
        Assert.That(
            result.FilePath,
            Does.EndWith(".sql.gz").IgnoreCase,
            "BackupResult.FilePath must use the .sql.gz extension");
        Assert.That(
            Path.GetFullPath(Path.GetDirectoryName(result.FilePath)!),
            Is.EqualTo(Path.GetFullPath(_outputDir)).IgnoreCase,
            "Provider must write the dump under DatabaseConfig.OutputPath");
        Assert.That(
            File.GetLastWriteTimeUtc(result.FilePath),
            Is.GreaterThanOrEqualTo(_testStartUtc.AddSeconds(-2)),
            "Returned FilePath must point to a file produced by this run, not a leftover");

        var plainSqlPath = Path.Combine(_outputDir, "restore.sql");
        await DecompressGzAsync(result.FilePath, plainSqlPath, _cts.Token);

        var restoreProvider = new MysqlRestoreProvider(
            NullLogger<MysqlRestoreProvider>.Instance,
            _resolver,
            _runner);

        await restoreProvider.ValidateRestoreSourceAsync(_connection, plainSqlPath, _cts.Token);
        await restoreProvider.PrepareTargetDatabaseAsync(_connection, _dstDb, _cts.Token);
        await restoreProvider.RestoreAsync(_connection, _dstDb, plainSqlPath, _cts.Token);

        var restoredRows = await ReadItemsAsync(_connection, _dstDb, _cts.Token);

        Assert.That(restoredRows, Is.EquivalentTo(ExpectedRows));
    }

    private static readonly (int Id, string Name)[] ExpectedRows =
    [
        (1, "alpha"),
        (2, "beta"),
        (3, "gamma"),
    ];

    private static async Task CreateSourceDatabaseAsync(ConnectionConfig connection, string dbName, CancellationToken ct)
    {
        await ExecuteOnServerAsync(
            connection,
            $"CREATE DATABASE `{Escape(dbName)}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;",
            ct);

        const string ddl = @"
CREATE TABLE items (
    id INT NOT NULL PRIMARY KEY,
    name VARCHAR(100) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";
        await ExecuteOnDatabaseAsync(connection, dbName, ddl, ct);

        foreach (var (id, name) in ExpectedRows)
        {
            await using var conn = new MySqlConnection(BuildConnectionString(connection, dbName));
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand("INSERT INTO items (id, name) VALUES (@id, @name);", conn);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", name);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<List<(int Id, string Name)>> ReadItemsAsync(
        ConnectionConfig connection, string dbName, CancellationToken ct)
    {
        var rows = new List<(int, string)>();
        await using var conn = new MySqlConnection(BuildConnectionString(connection, dbName));
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand("SELECT id, name FROM items;", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        return rows;
    }

    private static async Task DecompressGzAsync(string gzPath, string outputPath, CancellationToken ct)
    {
        await using var input = File.OpenRead(gzPath);
        await using var gz = new GZipStream(input, CompressionMode.Decompress);
        await using var output = File.Create(outputPath);
        await gz.CopyToAsync(output, ct);
    }

    private static async Task DropDatabaseIfExistsAsync(ConnectionConfig connection, string dbName, CancellationToken ct)
    {
        await ExecuteOnServerAsync(connection, $"DROP DATABASE IF EXISTS `{Escape(dbName)}`;", ct);
    }

    private static async Task DropLeftoverTestDatabasesAsync(ConnectionConfig connection, CancellationToken ct)
    {
        var leftovers = new List<string>();
        await using (var conn = new MySqlConnection(BuildServerConnectionString(connection)))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand(
                "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME LIKE @prefix;", conn);
            cmd.Parameters.AddWithValue("@prefix", TestDbPrefix + "%");
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

    private static async Task ExecuteOnServerAsync(ConnectionConfig connection, string sql, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(BuildServerConnectionString(connection));
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteOnDatabaseAsync(
        ConnectionConfig connection, string dbName, string sql, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(BuildConnectionString(connection, dbName));
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string BuildServerConnectionString(ConnectionConfig connection) =>
        new MySqlConnectionStringBuilder
        {
            Server = connection.Host,
            Port = (uint)connection.Port,
            UserID = connection.Username,
            Password = connection.Password,
        }.ToString();

    private static string BuildConnectionString(ConnectionConfig connection, string database) =>
        new MySqlConnectionStringBuilder
        {
            Server = connection.Host,
            Port = (uint)connection.Port,
            UserID = connection.Username,
            Password = connection.Password,
            Database = database,
        }.ToString();

    private static string Escape(string identifier) => identifier.Replace("`", "``");
}
