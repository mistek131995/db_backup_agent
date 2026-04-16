using DbBackupAgent;
using DbBackupAgent.Models;
using DbBackupAgent.Providers;
using DbBackupAgent.Services;
using DbBackupAgent.Settings;
using DbBackupAgent.Workers;
using SftpSettings = DbBackupAgent.Models.SftpSettings;
using UploadSettings = DbBackupAgent.Models.UploadSettings;

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

builder.Services.Configure<List<DatabaseConfig>>(
    builder.Configuration.GetSection("Databases"));

builder.Services.Configure<EncryptionSettings>(
    builder.Configuration.GetSection("EncryptionSettings"));

builder.Services.Configure<S3Settings>(
    builder.Configuration.GetSection("S3Settings"));

builder.Services.Configure<SftpSettings>(
    builder.Configuration.GetSection("SftpSettings"));

builder.Services.Configure<UploadSettings>(
    builder.Configuration.GetSection("UploadSettings"));

builder.Services.Configure<AgentSettings>(
    builder.Configuration.GetSection("AgentSettings"));

builder.Services.AddSingleton<PostgresBackupProvider>();
builder.Services.AddSingleton<MssqlBackupProvider>();
builder.Services.AddSingleton<IBackupProviderFactory, BackupProviderFactory>();

builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddSingleton<ContentDefinedChunker>();
builder.Services.AddSingleton<FileBackupService>();
builder.Services.AddSingleton<ManifestStore>();
builder.Services.AddSingleton<S3UploadService>();
builder.Services.AddSingleton<SftpUploadService>();
builder.Services.AddSingleton<IUploadServiceFactory, UploadServiceFactory>();
builder.Services.AddHttpClient<ReportService>();
builder.Services.AddHttpClient<ScheduleService>();
builder.Services.AddSingleton<BackupJob>();
builder.Services.AddHostedService<BackupWorker>();

var host = builder.Build();
host.Run();
