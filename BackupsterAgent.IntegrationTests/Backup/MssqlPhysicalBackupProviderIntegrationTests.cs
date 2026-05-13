using BackupsterAgent.Configuration;
using BackupsterAgent.Providers.Backup;
using BackupsterAgent.Providers.Restore;
using BackupsterAgent.Services.Common.Resolvers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupsterAgent.IntegrationTests.Backup;

[TestFixture]
[Category("Integration")]
public sealed class MssqlPhysicalBackupProviderIntegrationTests
{
    private const string TestDbPrefix = "bp_itest_physical_";

    private ConnectionConfig _connection = null!;
    private string _srcDb = null!;
    private string _dstDb = null!;
    private DateTime _testStartUtc;
    private CancellationTokenSource _cts = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        Assume.That(
            IntegrationConfig.TryGetMssqlConnection(out var connection),
            Is.True,
            "Mssql:* not configured; set via dotnet user-secrets or BACKUPSTER_INTEGRATION_MSSQL__* env vars.");

        _connection = connection;
        using var bootCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await DropLeftoverTestDatabasesAsync(_connection, bootCts.Token);
    }

    [SetUp]
    public async Task SetUp()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        _srcDb = TestDbPrefix + "src_" + suffix;
        _dstDb = TestDbPrefix + "dst_" + suffix;

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
    }

    [Test]
    public async Task BackupAsync_RoundTrip_RestoreRestoresContent()
    {
        var provider = new MssqlPhysicalBackupProvider(NullLogger<MssqlPhysicalBackupProvider>.Instance);

        var config = new DatabaseConfig
        {
            ConnectionName = _connection.Name,
            StorageName = "n/a",
            Database = _srcDb,
            OutputPath = string.Empty,
        };

        var result = await provider.BackupAsync(config, _connection, _cts.Token);

        Assert.That(result.Success, Is.True);
        Assert.That(File.Exists(result.FilePath), Is.True, "bak file must be visible to the agent after backup");
        Assert.That(result.SizeBytes, Is.GreaterThan(0));
        Assert.That(
            Path.GetExtension(result.FilePath),
            Is.EqualTo(".bak").IgnoreCase,
            "BackupResult.FilePath must use the .bak extension");

        var agentDir = await MssqlSharedPathResolver.GetAgentDirAsync(_connection, _cts.Token);
        Assert.That(
            Path.GetFullPath(Path.GetDirectoryName(result.FilePath)!).TrimEnd('\\', '/'),
            Is.EqualTo(Path.GetFullPath(agentDir).TrimEnd('\\', '/')).IgnoreCase,
            "Provider must write the bak under the agent-visible backup directory resolved by MssqlSharedPathResolver");
        Assert.That(
            File.GetLastWriteTimeUtc(result.FilePath),
            Is.GreaterThanOrEqualTo(_testStartUtc.AddSeconds(-2)),
            "Returned FilePath must point to a file produced by this run, not a leftover");

        var sqlDir = await MssqlSharedPathResolver.GetSqlDirAsync(_connection, _cts.Token);
        var sqlBakPath = MssqlSharedPathResolver.JoinSqlPath(sqlDir, Path.GetFileName(result.FilePath));

        try
        {
            var restoreProvider = new MssqlPhysicalRestoreProvider(
                NullLogger<MssqlPhysicalRestoreProvider>.Instance);

            await restoreProvider.ValidateRestoreSourceAsync(_connection, sqlBakPath, _cts.Token);
            await restoreProvider.PrepareTargetDatabaseAsync(_connection, _dstDb, _cts.Token);
            await restoreProvider.RestoreAsync(_connection, _dstDb, sqlBakPath, _cts.Token);

            var restoredRows = await ReadItemsAsync(_connection, _dstDb, _cts.Token);

            Assert.That(restoredRows, Is.EquivalentTo(ExpectedRows));
        }
        finally
        {
            await TryDeleteBakFileAsync(_connection, result.FilePath, sqlBakPath);
        }
    }

    private static readonly (int Id, string Name)[] ExpectedRows =
    [
        (1, "alpha"),
        (2, "beta"),
        (3, "gamma"),
    ];

    private static async Task CreateSourceDatabaseAsync(ConnectionConfig connection, string dbName, CancellationToken ct)
    {
        await ExecuteOnMasterAsync(connection, $"CREATE DATABASE [{Escape(dbName)}];", ct);

        const string ddl = @"
CREATE TABLE dbo.Items (
    Id   INT NOT NULL PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL
);";
        await ExecuteOnDatabaseAsync(connection, dbName, ddl, ct);

        foreach (var (id, name) in ExpectedRows)
        {
            await using var conn = new SqlConnection(BuildConnectionString(connection, dbName));
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand("INSERT INTO dbo.Items (Id, Name) VALUES (@id, @name);", conn);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", name);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<List<(int Id, string Name)>> ReadItemsAsync(ConnectionConfig connection, string dbName, CancellationToken ct)
    {
        var rows = new List<(int, string)>();
        await using var conn = new SqlConnection(BuildConnectionString(connection, dbName));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT Id, Name FROM dbo.Items;", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        return rows;
    }

    private static async Task DropDatabaseIfExistsAsync(ConnectionConfig connection, string dbName, CancellationToken ct)
    {
        var sql = $@"
IF DB_ID(N'{EscapeForString(dbName)}') IS NOT NULL
BEGIN
    ALTER DATABASE [{Escape(dbName)}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{Escape(dbName)}];
END";
        await ExecuteOnMasterAsync(connection, sql, ct);
    }

    private static async Task DropLeftoverTestDatabasesAsync(ConnectionConfig connection, CancellationToken ct)
    {
        var leftovers = new List<string>();
        await using (var conn = new SqlConnection(BuildMasterConnectionString(connection)))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                $"SELECT name FROM sys.databases WHERE name LIKE N'{TestDbPrefix}%';", conn);
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

    private static async Task TryDeleteBakFileAsync(ConnectionConfig connection, string agentPath, string sqlPath)
    {
        try { if (File.Exists(agentPath)) File.Delete(agentPath); }
        catch (Exception ex) { TestContext.Progress.WriteLine($"Local bak delete '{agentPath}' failed: {ex.Message}"); }

        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var conn = new SqlConnection(BuildMasterConnectionString(connection));
            await conn.OpenAsync(cleanupCts.Token);
            await using var cmd = new SqlCommand(
                $"EXEC master.sys.xp_delete_files N'{EscapeForString(sqlPath)}';", conn)
            { CommandTimeout = 30 };
            await cmd.ExecuteNonQueryAsync(cleanupCts.Token);
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"SQL-side bak delete '{sqlPath}' failed: {ex.Message}");
        }
    }

    private static async Task ExecuteOnMasterAsync(ConnectionConfig connection, string sql, CancellationToken ct)
    {
        await using var conn = new SqlConnection(BuildMasterConnectionString(connection));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteOnDatabaseAsync(ConnectionConfig connection, string dbName, string sql, CancellationToken ct)
    {
        await using var conn = new SqlConnection(BuildConnectionString(connection, dbName));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string BuildMasterConnectionString(ConnectionConfig connection) =>
        BuildConnectionString(connection, "master");

    private static string BuildConnectionString(ConnectionConfig connection, string database) =>
        new SqlConnectionStringBuilder
        {
            DataSource = $"{connection.Host},{connection.Port}",
            InitialCatalog = database,
            UserID = connection.Username,
            Password = connection.Password,
            Encrypt = true,
            TrustServerCertificate = true,
        }.ConnectionString;

    private static string Escape(string identifier) => identifier.Replace("]", "]]");
    private static string EscapeForString(string s) => s.Replace("'", "''");
}
