using System.Security.Cryptography;
using DbBackupAgent.Configuration;
using DbBackupAgent.Contracts;
using DbBackupAgent.Enums;
using DbBackupAgent.Exceptions;
using DbBackupAgent.Providers;
using DbBackupAgent.Services;
using DbBackupAgent.Services.Common;
using DbBackupAgent.Services.Restore;
using DbBackupAgent.Services.Upload;
using DbBackupAgent.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DbBackupAgent.Tests.Services;

[TestFixture]
public sealed class DatabaseRestoreServiceTests
{
    private string _tempRoot = null!;
    private byte[] _key = null!;
    private EncryptionService _encryption = null!;
    private FakeUploadService _upload = null!;
    private StubRestoreProvider _provider = null!;
    private StubRestoreProviderFactory _factory = null!;

    private const string PgConnectionName = "pg-main";
    private const string OtherPgConnectionName = "pg-reporting";

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "dbbackup-db-restore-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);

        _key = RandomNumberGenerator.GetBytes(32);
        _encryption = new EncryptionService(
            Options.Create(new EncryptionSettings { Key = Convert.ToBase64String(_key) }),
            NullLogger<EncryptionService>.Instance);

        _upload = new FakeUploadService();
        _provider = new StubRestoreProvider();
        _factory = new StubRestoreProviderFactory(_provider);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ---------- ResolveTargetConnection ----------

    [Test]
    public void ResolveTargetConnection_ExplicitTargetConnectionName_UsedDirectly()
    {
        var service = BuildService(
            connections:
            [
                ConnPg(PgConnectionName),
                ConnPg(OtherPgConnectionName),
            ],
            databases:
            [
                new DatabaseConfig { ConnectionName = PgConnectionName, Database = "main_db" },
            ]);

        var task = new RestoreTaskForAgentDto
        {
            TaskId = Guid.NewGuid(),
            SourceDatabaseName = "main_db",
            DumpObjectKey = "k",
            TargetConnectionName = OtherPgConnectionName,
        };

        var resolved = service.ResolveTargetConnection(task);

        Assert.That(resolved.Name, Is.EqualTo(OtherPgConnectionName));
    }

    [Test]
    public void ResolveTargetConnection_NoTargetName_ResolvesViaDatabaseConfig()
    {
        var service = BuildService(
            connections: [ConnPg(PgConnectionName)],
            databases:
            [
                new DatabaseConfig { ConnectionName = PgConnectionName, Database = "main_db" },
            ]);

        var task = new RestoreTaskForAgentDto
        {
            TaskId = Guid.NewGuid(),
            SourceDatabaseName = "main_db",
            DumpObjectKey = "k",
        };

        var resolved = service.ResolveTargetConnection(task);

        Assert.That(resolved.Name, Is.EqualTo(PgConnectionName));
    }

    [Test]
    public void ResolveTargetConnection_UnknownSourceAndNoTarget_ThrowsWithExplicitHint()
    {
        var service = BuildService(
            connections: [ConnPg(PgConnectionName)],
            databases: []);

        var task = new RestoreTaskForAgentDto
        {
            TaskId = Guid.NewGuid(),
            SourceDatabaseName = "missing_db",
            DumpObjectKey = "k",
        };

        var ex = Assert.Throws<InvalidOperationException>(() => service.ResolveTargetConnection(task));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("missing_db"));
            Assert.That(ex.Message, Does.Contain("TargetConnectionName"));
        });
    }

    // ---------- BuildTempDir ----------

    [Test]
    public void BuildTempDir_NullTempPath_UsesBaseDirectoryTempSubfolder()
    {
        var taskId = Guid.NewGuid();

        var path = DatabaseRestoreService.BuildTempDir(null, taskId);

        Assert.Multiple(() =>
        {
            Assert.That(Path.IsPathRooted(path), Is.True);
            Assert.That(path, Does.StartWith(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)));
            Assert.That(path, Does.EndWith(Path.Combine("temp", taskId.ToString("N"))));
        });
    }

    [Test]
    public void BuildTempDir_RelativeTempPath_CombinedWithBaseDirectory()
    {
        var taskId = Guid.NewGuid();

        var path = DatabaseRestoreService.BuildTempDir("my-restore", taskId);

        Assert.Multiple(() =>
        {
            Assert.That(Path.IsPathRooted(path), Is.True);
            Assert.That(path, Does.StartWith(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)));
            Assert.That(path, Does.Contain("my-restore"));
            Assert.That(path, Does.EndWith(taskId.ToString("N")));
        });
    }

    [Test]
    public void BuildTempDir_AbsoluteTempPath_UsedAsIs()
    {
        var taskId = Guid.NewGuid();
        var absolute = Path.Combine(Path.GetTempPath(), "restore-abs");

        var path = DatabaseRestoreService.BuildTempDir(absolute, taskId);

        Assert.That(path, Is.EqualTo(Path.Combine(absolute, taskId.ToString("N"))));
    }

    // ---------- IsKnownMssqlPermissionCode ----------

    [TestCase(229)]
    [TestCase(262)]
    [TestCase(300)]
    [TestCase(916)]
    [TestCase(15247)]
    [TestCase(21089)]
    public void IsKnownMssqlPermissionCode_WhitelistedCodes_ReturnTrue(int code)
    {
        Assert.That(DatabaseRestoreService.IsKnownMssqlPermissionCode(code), Is.True);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(228)]
    [TestCase(8152)]
    [TestCase(-1)]
    [TestCase(50000)]
    public void IsKnownMssqlPermissionCode_OtherCodes_ReturnFalse(int code)
    {
        Assert.That(DatabaseRestoreService.IsKnownMssqlPermissionCode(code), Is.False);
    }

    // ---------- RunAsync error mapping ----------

    [Test]
    public async Task RunAsync_RestorePermissionExceptionFromProvider_MapsToFailedWithExMessage()
    {
        const string detail = "Недостаточно прав custom-message";
        _provider.OnValidatePermissions = () => throw new RestorePermissionException(detail);

        var service = BuildService(
            connections: [ConnPg(PgConnectionName)],
            databases: [new DatabaseConfig { ConnectionName = PgConnectionName, Database = "db1" }]);

        var result = await service.RunAsync(NewTask("db1"), _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Is.EqualTo(detail));
        });
        AssertTempDirCleaned();
    }

    [Test]
    public async Task RunAsync_PostgresPermissionDenied42501_MapsToRoleHintMessage()
    {
        _provider.OnValidatePermissions = () =>
            throw new PostgresException(
                messageText: "permission denied for database",
                severity: "ERROR",
                invariantSeverity: "ERROR",
                sqlState: "42501");

        var service = BuildService(
            connections: [ConnPg(PgConnectionName)],
            databases: [new DatabaseConfig { ConnectionName = PgConnectionName, Database = "db1" }]);

        var result = await service.RunAsync(NewTask("db1"), _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("Недостаточно прав"));
            Assert.That(result.ErrorMessage, Does.Contain("CREATEDB"));
            Assert.That(result.ErrorMessage, Does.Contain("pg_signal_backend"));
        });
    }

    [Test]
    public async Task RunAsync_AuthTagMismatchOnDecrypt_MapsToEncryptionKeyHintMessage()
    {
        // Valid encrypted file, tampered inside a frame's ciphertext.
        var sourcePath = await CreateDumpPayloadForEncryptionAsync(bytes: new byte[] { 1, 2, 3, 4, 5 });
        var encPath = await _encryption.EncryptAsync(sourcePath, CancellationToken.None);
        var encBytes = await File.ReadAllBytesAsync(encPath);
        // Header (8) + nonce (12) + ciphertext — flip a byte inside ciphertext.
        encBytes[8 + 12 + 1] ^= 0xFF;
        _upload.FileBytes["dump.enc.key"] = encBytes;

        var service = BuildService(
            connections: [ConnPg(PgConnectionName)],
            databases: [new DatabaseConfig { ConnectionName = PgConnectionName, Database = "db1" }]);

        var task = NewTaskWithKey("db1", "dump.enc.key");
        var result = await service.RunAsync(task, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("расшифровать"));
            Assert.That(result.ErrorMessage, Does.Contain("EncryptionKey"));
        });
        AssertTempDirCleaned();
    }

    [Test]
    public async Task RunAsync_BadMagicHeader_MapsToUnsupportedFormatMessage()
    {
        // 8 bytes with non-BK01 magic — InvalidDataException("Bad magic ...").
        _upload.FileBytes["dump.enc.key"] = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0x10, 0 };

        var service = BuildService(
            connections: [ConnPg(PgConnectionName)],
            databases: [new DatabaseConfig { ConnectionName = PgConnectionName, Database = "db1" }]);

        var task = NewTaskWithKey("db1", "dump.enc.key");
        var result = await service.RunAsync(task, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("не поддерживается"));
            Assert.That(result.ErrorMessage, Does.Contain("Обновите агент"));
        });
    }

    [Test]
    public async Task RunAsync_TruncatedHeader_MapsToCorruptedFileMessage()
    {
        // 3 bytes — InvalidDataException("Encrypted file is truncated ..."), no "Bad magic".
        _upload.FileBytes["dump.enc.key"] = new byte[] { 0x42, 0x4B, 0x30 };

        var service = BuildService(
            connections: [ConnPg(PgConnectionName)],
            databases: [new DatabaseConfig { ConnectionName = PgConnectionName, Database = "db1" }]);

        var task = NewTaskWithKey("db1", "dump.enc.key");
        var result = await service.RunAsync(task, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("повреждён").Or.Contain("усечён"));
            Assert.That(result.ErrorMessage, Does.Not.Contain("Bad magic"));
            Assert.That(result.ErrorMessage, Does.Not.Contain("Обновите агент"));
        });
    }

    [Test]
    public async Task RunAsync_GenericProviderException_MapsToGenericFailedMessage()
    {
        _provider.OnValidatePermissions = () => throw new InvalidOperationException("boom");

        var service = BuildService(
            connections: [ConnPg(PgConnectionName)],
            databases: [new DatabaseConfig { ConnectionName = PgConnectionName, Database = "db1" }]);

        var result = await service.RunAsync(NewTask("db1"), _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("Ошибка восстановления БД"));
            Assert.That(result.ErrorMessage, Does.Contain("boom"));
        });
    }

    [Test]
    public void RunAsync_CancellationPreAcquired_PropagatesCancellation()
    {
        var service = BuildService(
            connections: [ConnPg(PgConnectionName)],
            databases: [new DatabaseConfig { ConnectionName = PgConnectionName, Database = "db1" }]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(
            () => service.RunAsync(NewTask("db1"), _upload, TestHelpers.NullReporter<RestoreStage>(), cts.Token));
        Assert.That(_provider.ValidateCalls, Is.EqualTo(1),
            "provider must be invoked with the cancelled token so cancellation is observed, not hand-fed");
    }

    [Test]
    public async Task RunAsync_TempDir_CleanedUpInFinallyOnFailure()
    {
        _provider.OnValidatePermissions = () => throw new InvalidOperationException("boom");

        var restoreSettings = new RestoreSettings { TempPath = _tempRoot };
        var service = BuildService(
            connections: [ConnPg(PgConnectionName)],
            databases: [new DatabaseConfig { ConnectionName = PgConnectionName, Database = "db1" }],
            restoreSettings: restoreSettings);

        var task = NewTask("db1");
        await service.RunAsync(task, _upload, TestHelpers.NullReporter<RestoreStage>(), CancellationToken.None);

        var taskDir = Path.Combine(_tempRoot, task.TaskId.ToString("N"));
        Assert.That(Directory.Exists(taskDir), Is.False, "per-task temp directory must be cleaned in finally");
    }

    // ---------- helpers ----------

    private DatabaseRestoreService BuildService(
        IEnumerable<ConnectionConfig> connections,
        IEnumerable<DatabaseConfig> databases,
        RestoreSettings? restoreSettings = null)
    {
        var resolver = new ConnectionResolver(connections);
        return new DatabaseRestoreService(
            resolver,
            _factory,
            _encryption,
            Options.Create(restoreSettings ?? new RestoreSettings { TempPath = _tempRoot }),
            Options.Create(databases.ToList()),
            NullLogger<DatabaseRestoreService>.Instance);
    }

    private static ConnectionConfig ConnPg(string name) => new()
    {
        Name = name,
        DatabaseType = DatabaseType.Postgres,
        Host = "127.0.0.1",
        Port = 5432,
        Username = "u",
        Password = "p",
    };

    private static RestoreTaskForAgentDto NewTask(string sourceDb) =>
        NewTaskWithKey(sourceDb, "dump.enc.key");

    private static RestoreTaskForAgentDto NewTaskWithKey(string sourceDb, string dumpObjectKey) => new()
    {
        TaskId = Guid.NewGuid(),
        SourceDatabaseName = sourceDb,
        DumpObjectKey = dumpObjectKey,
    };

    private async Task<string> CreateDumpPayloadForEncryptionAsync(byte[] bytes)
    {
        var path = Path.Combine(_tempRoot, "plain-" + Path.GetRandomFileName() + ".bin");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private void AssertTempDirCleaned()
    {
        // Any per-task subdirs under _tempRoot must be gone (parent itself is recreated as needed).
        var subdirs = Directory.Exists(_tempRoot)
            ? Directory.GetDirectories(_tempRoot)
            : [];
        Assert.That(subdirs, Is.Empty, "per-task temp directory must be cleaned in finally");
    }

    private sealed class StubRestoreProvider : IRestoreProvider
    {
        public Action? OnValidatePermissions { get; set; }
        public Action? OnPrepareTarget { get; set; }
        public Action? OnRestore { get; set; }

        public int ValidateCalls { get; private set; }
        public int PrepareCalls { get; private set; }
        public int RestoreCalls { get; private set; }

        public Task ValidatePermissionsAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct)
        {
            ValidateCalls++;
            ct.ThrowIfCancellationRequested();
            OnValidatePermissions?.Invoke();
            return Task.CompletedTask;
        }

        public Task PrepareTargetDatabaseAsync(ConnectionConfig connection, string targetDatabase, CancellationToken ct)
        {
            PrepareCalls++;
            ct.ThrowIfCancellationRequested();
            OnPrepareTarget?.Invoke();
            return Task.CompletedTask;
        }

        public Task RestoreAsync(ConnectionConfig connection, string targetDatabase, string restoreFilePath, CancellationToken ct)
        {
            RestoreCalls++;
            ct.ThrowIfCancellationRequested();
            OnRestore?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class StubRestoreProviderFactory(IRestoreProvider provider) : IRestoreProviderFactory
    {
        public IRestoreProvider GetProvider(DatabaseType databaseType) => provider;
    }

    private sealed class FakeUploadService : IUploadService
    {
        public Dictionary<string, byte[]> FileBytes { get; } = [];

        public async Task DownloadAsync(string objectKey, string localPath, IProgress<long>? progress, CancellationToken ct)
        {
            if (!FileBytes.TryGetValue(objectKey, out var bytes))
                throw new FileNotFoundException($"Fake S3: '{objectKey}' not found.", objectKey);
            await File.WriteAllBytesAsync(localPath, bytes, ct);
        }

        public Task<byte[]> DownloadBytesAsync(string objectKey, CancellationToken ct) =>
            throw new NotSupportedException("DatabaseRestoreService must not call DownloadBytesAsync");

        public Task<string> UploadAsync(string filePath, string folder, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UploadBytesAsync(byte[] content, string objectKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string objectKey, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
