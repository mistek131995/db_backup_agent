using BackupsterAgent.Enums;

namespace BackupsterAgent.Services.Dashboard.Clients;

public readonly record struct ScheduleEntry(BackupMode Mode, DateTime NextRun);
