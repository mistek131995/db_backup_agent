using System.Diagnostics;
using DbBackupAgent.Configuration;
using DbBackupAgent.Enums;
using DbBackupAgent.Providers;
using DbBackupAgent.Services;
using DbBackupAgent.Services.Common;
using DbBackupAgent.Settings;
using DbBackupAgent.Workers;
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

// Env vars must win over the external config file
builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<List<ConnectionConfig>>(
    builder.Configuration.GetSection("Connections"));

builder.Services.Configure<List<DatabaseConfig>>(
    builder.Configuration.GetSection("Databases"));

builder.Services.Configure<EncryptionSettings>(
    builder.Configuration.GetSection("EncryptionSettings"));

builder.Services.Configure<S3Settings>(
    builder.Configuration.GetSection("S3Settings"));

builder.Services.Configure<SftpSettings>(
    builder.Configuration.GetSection("SftpSettings"));

builder.Services.AddOptions<UploadSettings>()
    .Bind(builder.Configuration.GetSection("UploadSettings"))
    .Validate(s => Enum.IsDefined(s.Provider),
        $"UploadSettings:Provider must be one of: {string.Join(", ", Enum.GetNames<UploadProvider>())}")
    .ValidateOnStart();

builder.Services.Configure<AgentSettings>(
    builder.Configuration.GetSection("AgentSettings"));

builder.Services.Configure<RestoreSettings>(
    builder.Configuration.GetSection("RestoreSettings"));

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
    ShouldListenTo = source => source.Name == "DbBackupAgent",
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
});

builder.Services.AddSingleton(new ActivitySource("DbBackupAgent"));

builder.Services.AddSingleton<IAgentActivityLock, AgentActivityLock>();
builder.Services.AddSingleton(sp =>
    new ConnectionResolver(sp.GetRequiredService<IOptions<List<ConnectionConfig>>>().Value));
builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddSingleton<ContentDefinedChunker>();
builder.Services.AddSingleton<FileBackupService>();
builder.Services.AddSingleton<ManifestStore>();
builder.Services.AddSingleton<S3UploadService>();
builder.Services.AddSingleton<SftpUploadService>();
builder.Services.AddSingleton<IUploadServiceFactory, UploadServiceFactory>();
builder.Services.AddSingleton<IUploadService>(sp => sp.GetRequiredService<S3UploadService>());
builder.Services.AddHttpClient<ReportService>();
builder.Services.AddHttpClient<ScheduleService>();
builder.Services.AddHttpClient<IConnectionSyncService, ConnectionSyncService>();
builder.Services.AddHttpClient<IRestoreTaskClient, RestoreTaskClient>();
builder.Services.AddSingleton<DatabaseRestoreService>();
builder.Services.AddSingleton<FileRestoreService>();
builder.Services.AddSingleton<BackupJob>();
builder.Services.AddHostedService<BackupWorker>();
builder.Services.AddHostedService<ConnectionSyncWorker>();
builder.Services.AddHostedService<RestoreTaskPollingService>();

var host = builder.Build();
host.Run();
