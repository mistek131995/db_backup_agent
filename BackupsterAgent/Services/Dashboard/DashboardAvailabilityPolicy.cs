namespace BackupsterAgent.Services.Dashboard;

public static class DashboardAvailabilityPolicy
{
    public static DashboardAvailability ClassifyResponse(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (code >= 200 && code <= 299) return DashboardAvailability.Ok;
        if (code >= 500 && code <= 599) return DashboardAvailability.OfflineRetryable;
        return DashboardAvailability.PermanentSkip;
    }

    public static DashboardAvailability ClassifyException(Exception exception) =>
        exception switch
        {
            DashboardUnauthorizedException => DashboardAvailability.PermanentSkip,
            HttpRequestException => DashboardAvailability.OfflineRetryable,
            TaskCanceledException => DashboardAvailability.OfflineRetryable,
            _ => DashboardAvailability.PermanentSkip,
        };
}
