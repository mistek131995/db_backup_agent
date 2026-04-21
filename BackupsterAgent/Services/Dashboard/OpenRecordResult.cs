namespace BackupsterAgent.Services.Dashboard;

public sealed record OpenRecordResult(DashboardAvailability Status, Guid? Id = null);
