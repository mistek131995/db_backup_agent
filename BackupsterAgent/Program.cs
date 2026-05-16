using System.Diagnostics;
using BackupsterAgent.Configuration;
using BackupsterAgent.Providers;
using BackupsterAgent.Providers.Backup;
using BackupsterAgent.Providers.Restore;
using BackupsterAgent.Services;
using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Backup.Coordinator;
using BackupsterAgent.Providers.Upload;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Common.Outbox;
using BackupsterAgent.Services.Common.Processes;
using BackupsterAgent.Services.Common.Progress;
using BackupsterAgent.Services.Common.Resolvers;
using BackupsterAgent.Services.Common.Security;
using BackupsterAgent.Services.Common.State;
using BackupsterAgent.Services.Dashboard;
using BackupsterAgent.Services.Dashboard.Clients;
using BackupsterAgent.Services.Dashboard.Sync;
using BackupsterAgent.Services.Restore;
using BackupsterAgent.Workers;
using BackupsterAgent.Workers.Handlers;
using Microsoft.Extensions.Options;

var defaultConfigDir = OperatingSystem.IsWindows()
    ? Path.Combine(AppContext.BaseDirectory, "config")
    : "/app/config";
var configDir = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? defaultConfigDir;
ConfigBootstrapper.EnsureTemplate(configDir);

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService();
builder.Services.AddSystemd();

builder.Configuration.AddJsonFile(
    Path.Combine(configDir, "appsettings.json"), optional: true, reloadOnChange: false);

builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<List<ConnectionConfig>>(
    builder.Configuration.GetSection("Connections"));

builder.Services.Configure<List<StorageConfig>>(
    builder.Configuration.GetSection("Storages"));

builder.Services.Configure<List<DatabaseConfig>>(
    builder.Configuration.GetSection("Databases"));

builder.Services.Configure<List<FileSetConfig>>(
    builder.Configuration.GetSection("FileSets"));

builder.Services.Configure<EncryptionSettings>(
    builder.Configuration.GetSection("EncryptionSettings"));

builder.Services.Configure<AgentSettings>(
    builder.Configuration.GetSection("AgentSettings"));

builder.Services.Configure<RestoreSettings>(
    builder.Configuration.GetSection("RestoreSettings"));

builder.Services.Configure<GcSettings>(
    builder.Configuration.GetSection("GcSettings"));

builder.Services.Configure<RetentionSettings>(
    builder.Configuration.GetSection("RetentionSettings"));

builder.Services.Configure<OutboxSettings>(
    builder.Configuration.GetSection("OutboxSettings"));

builder.Services.AddSingleton<PostgresLogicalBackupProvider>();
builder.Services.AddSingleton<PostgresPhysicalBackupProvider>();
builder.Services.AddSingleton<MssqlPhysicalBackupProvider>();
builder.Services.AddSingleton<MssqlLogicalBackupProvider>();
builder.Services.AddSingleton<MysqlLogicalBackupProvider>();
builder.Services.AddSingleton<MysqlPhysicalBackupProvider>();
builder.Services.AddSingleton<IBackupProviderFactory, BackupProviderFactory>();

builder.Services.AddSingleton<PostgresRestoreProvider>();
builder.Services.AddSingleton<PostgresPhysicalRestoreProvider>();
builder.Services.AddSingleton<MssqlPhysicalRestoreProvider>();
builder.Services.AddSingleton<MssqlLogicalRestoreProvider>();
builder.Services.AddSingleton<MysqlRestoreProvider>();
builder.Services.AddSingleton<MysqlPhysicalRestoreProvider>();
builder.Services.AddSingleton<IRestoreProviderFactory, RestoreProviderFactory>();

ActivitySource.AddActivityListener(new ActivityListener
{
    ShouldListenTo = source => source.Name == "BackupsterAgent",
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
});

builder.Services.AddSingleton(new ActivitySource("BackupsterAgent"));

builder.Services.AddSingleton<IAgentActivityLock, AgentActivityLock>();
var runsDir = Path.Combine(configDir, "runs");
builder.Services.AddSingleton(sp =>
    new RunStateStore(runsDir, sp.GetRequiredService<ILogger<RunStateStore>>()));
builder.Services.AddSingleton<IBackupRunTracker>(sp =>
    new BackupRunTracker(
        sp.GetRequiredService<RunStateStore>(),
        sp.GetRequiredService<ILogger<BackupRunTracker>>()));

var outboxDir = Path.Combine(configDir, "outbox");
builder.Services.AddSingleton<IOutboxStore>(sp =>
    new OutboxStore(outboxDir, sp.GetRequiredService<ILogger<OutboxStore>>()));
builder.Services.AddSingleton<PostgresBinaryResolver>();
builder.Services.AddSingleton<MysqlBinaryResolver>();
builder.Services.AddSingleton<IExternalProcessRunner, ExternalProcessRunner>();
builder.Services.AddSingleton(sp =>
    new ConnectionResolver(sp.GetRequiredService<IOptions<List<ConnectionConfig>>>().Value));
builder.Services.AddSingleton(sp =>
    new StorageResolver(sp.GetRequiredService<IOptions<List<StorageConfig>>>().Value));
builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddSingleton<ContentDefinedChunker>();
builder.Services.AddSingleton<FileBackupService>();
builder.Services.AddSingleton<ManifestStore>();
builder.Services.AddSingleton<IUploadProviderFactory, UploadProviderFactory>();
builder.Services.AddSingleton<IDashboardAuthGuard, DashboardAuthGuard>();

void ConfigureDashboardClient(HttpClient c, int timeoutSeconds)
{
    c.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    c.DefaultRequestHeaders.Add("X-Agent-Version", AgentVersion.Current);
}

builder.Services.AddHttpClient<IBackupRecordClient, BackupRecordClient>(
    c => ConfigureDashboardClient(c, 20));
var scheduleCachePath = Path.Combine(configDir, "schedule.json");
builder.Services.AddSingleton(sp =>
    new ScheduleStore(scheduleCachePath, sp.GetRequiredService<ILogger<ScheduleStore>>()));
builder.Services.AddHttpClient<ScheduleService>(
    c => ConfigureDashboardClient(c, 20));
builder.Services.AddHttpClient<IConnectionSyncService, ConnectionSyncService>(
    c => ConfigureDashboardClient(c, 20));
builder.Services.AddHttpClient<IFileSetSyncService, FileSetSyncService>(
    c => ConfigureDashboardClient(c, 20));
builder.Services.AddHttpClient<IDatabaseSyncService, DatabaseSyncService>(
    c => ConfigureDashboardClient(c, 20));
builder.Services.AddHttpClient<IStorageSyncService, StorageSyncService>(
    c => ConfigureDashboardClient(c, 20));
builder.Services.AddHttpClient<IAgentTaskClient, AgentTaskClient>(
    c => ConfigureDashboardClient(c, 60));
builder.Services.AddHttpClient<IRetentionClient, RetentionClient>(
    c => ConfigureDashboardClient(c, 20));
builder.Services.AddSingleton<IProgressReporterFactory, ProgressReporterFactory>();
builder.Services.AddSingleton<DatabaseRestoreService>();
builder.Services.AddSingleton<FileRestoreService>();
builder.Services.AddSingleton<BackupDeleteService>();
builder.Services.AddSingleton<BackupRunCoordinator>();
builder.Services.AddSingleton<DatabaseBackupPipeline>();
builder.Services.AddSingleton<FileSetBackupPipeline>();
builder.Services.AddSingleton<BackupJob>();
builder.Services.AddSingleton<FileSetBackupJob>();
builder.Services.AddSingleton<IAgentTaskHandler, RestoreTaskHandler>();
builder.Services.AddSingleton<IAgentTaskHandler, DeleteTaskHandler>();
builder.Services.AddSingleton<IAgentTaskHandler, BackupTaskHandler>();
builder.Services.AddSingleton<IAgentTaskHandler, FileSetBackupTaskHandler>();
builder.Services.AddHostedService<BackupWorker>();
builder.Services.AddHostedService<FileSetWorker>();
builder.Services.AddHostedService<TopologySyncWorker>();
builder.Services.AddHostedService<RestoreTempCleanupService>();
builder.Services.AddHostedService<AgentTaskPollingService>();
builder.Services.AddHostedService<ChunkGcWorker>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddHostedService<OutboxReplayWorker>();

var host = builder.Build();

var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BackupsterAgent");
startupLogger.LogInformation("BackupsterAgent {Version} starting", AgentVersion.Current);

host.Run();
