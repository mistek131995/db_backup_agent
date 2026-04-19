using System.Diagnostics;
using BackupsterAgent.Configuration;
using BackupsterAgent.Providers;
using BackupsterAgent.Services;
using BackupsterAgent.Services.Backup;
using BackupsterAgent.Services.Common;
using BackupsterAgent.Services.Dashboard;
using BackupsterAgent.Services.Restore;
using BackupsterAgent.Services.Upload;
using BackupsterAgent.Settings;
using BackupsterAgent.Workers;
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

builder.Services.Configure<EncryptionSettings>(
    builder.Configuration.GetSection("EncryptionSettings"));

builder.Services.Configure<AgentSettings>(
    builder.Configuration.GetSection("AgentSettings"));

builder.Services.Configure<RestoreSettings>(
    builder.Configuration.GetSection("RestoreSettings"));

builder.Services.Configure<GcSettings>(
    builder.Configuration.GetSection("GcSettings"));

builder.Services.AddSingleton<PostgresBackupProvider>();
builder.Services.AddSingleton<MssqlBackupProvider>();
builder.Services.AddSingleton<MysqlBackupProvider>();
builder.Services.AddSingleton<IBackupProviderFactory, BackupProviderFactory>();

builder.Services.AddSingleton<PostgresRestoreProvider>();
builder.Services.AddSingleton<MssqlRestoreProvider>();
builder.Services.AddSingleton<MysqlRestoreProvider>();
builder.Services.AddSingleton<IRestoreProviderFactory, RestoreProviderFactory>();

ActivitySource.AddActivityListener(new ActivityListener
{
    ShouldListenTo = source => source.Name == "BackupsterAgent",
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
});

builder.Services.AddSingleton(new ActivitySource("BackupsterAgent"));

builder.Services.AddSingleton<IAgentActivityLock, AgentActivityLock>();
builder.Services.AddSingleton(sp =>
    new ConnectionResolver(sp.GetRequiredService<IOptions<List<ConnectionConfig>>>().Value));
builder.Services.AddSingleton(sp =>
    new StorageResolver(sp.GetRequiredService<IOptions<List<StorageConfig>>>().Value));
builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddSingleton<ContentDefinedChunker>();
builder.Services.AddSingleton<FileBackupService>();
builder.Services.AddSingleton<ManifestStore>();
builder.Services.AddSingleton<IUploadServiceFactory, UploadServiceFactory>();
builder.Services.AddSingleton<IDashboardAuthGuard, DashboardAuthGuard>();
builder.Services.AddHttpClient<IBackupRecordClient, BackupRecordClient>(
    c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient<ScheduleService>(
    c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient<IConnectionSyncService, ConnectionSyncService>(
    c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient<IRestoreTaskClient, RestoreTaskClient>(
    c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<IProgressReporterFactory, ProgressReporterFactory>();
builder.Services.AddSingleton<DatabaseRestoreService>();
builder.Services.AddSingleton<FileRestoreService>();
builder.Services.AddSingleton<ChunkGcService>();
builder.Services.AddSingleton<BackupJob>();
builder.Services.AddHostedService<BackupWorker>();
builder.Services.AddHostedService<ConnectionSyncWorker>();
builder.Services.AddHostedService<RestoreTaskPollingService>();
builder.Services.AddHostedService<ChunkGcWorker>();

var host = builder.Build();
host.Run();
