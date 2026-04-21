using System.Net;
using BackupsterAgent.Services.Dashboard;

namespace BackupsterAgent.Tests.Services;

[TestFixture]
public sealed class DashboardAvailabilityPolicyTests
{
    [TestCase(HttpStatusCode.OK)]
    [TestCase(HttpStatusCode.Created)]
    [TestCase(HttpStatusCode.NoContent)]
    public void ClassifyResponse_2xx_ReturnsOk(HttpStatusCode code)
    {
        using var response = new HttpResponseMessage(code);

        var result = DashboardAvailabilityPolicy.ClassifyResponse(response);

        Assert.That(result, Is.EqualTo(DashboardAvailability.Ok));
    }

    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.BadGateway)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    [TestCase(HttpStatusCode.GatewayTimeout)]
    [TestCase((HttpStatusCode)599)]
    public void ClassifyResponse_5xx_ReturnsOfflineRetryable(HttpStatusCode code)
    {
        using var response = new HttpResponseMessage(code);

        var result = DashboardAvailabilityPolicy.ClassifyResponse(response);

        Assert.That(result, Is.EqualTo(DashboardAvailability.OfflineRetryable));
    }

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.PaymentRequired)]
    [TestCase(HttpStatusCode.Forbidden)]
    [TestCase(HttpStatusCode.NotFound)]
    [TestCase(HttpStatusCode.Conflict)]
    [TestCase(HttpStatusCode.Gone)]
    [TestCase(HttpStatusCode.BadRequest)]
    public void ClassifyResponse_4xx_ReturnsPermanentSkip(HttpStatusCode code)
    {
        using var response = new HttpResponseMessage(code);

        var result = DashboardAvailabilityPolicy.ClassifyResponse(response);

        Assert.That(result, Is.EqualTo(DashboardAvailability.PermanentSkip));
    }

    [Test]
    public void ClassifyResponse_3xx_ReturnsPermanentSkip()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Redirect);

        var result = DashboardAvailabilityPolicy.ClassifyResponse(response);

        Assert.That(result, Is.EqualTo(DashboardAvailability.PermanentSkip));
    }

    [Test]
    public void ClassifyException_HttpRequestException_ReturnsOfflineRetryable()
    {
        var ex = new HttpRequestException("connection refused");

        var result = DashboardAvailabilityPolicy.ClassifyException(ex);

        Assert.That(result, Is.EqualTo(DashboardAvailability.OfflineRetryable));
    }

    [Test]
    public void ClassifyException_TaskCanceledException_ReturnsOfflineRetryable()
    {
        var ex = new TaskCanceledException("timeout");

        var result = DashboardAvailabilityPolicy.ClassifyException(ex);

        Assert.That(result, Is.EqualTo(DashboardAvailability.OfflineRetryable));
    }

    [Test]
    public void ClassifyException_DashboardUnauthorizedException_ReturnsPermanentSkip()
    {
        var ex = new DashboardUnauthorizedException("TestChannel");

        var result = DashboardAvailabilityPolicy.ClassifyException(ex);

        Assert.That(result, Is.EqualTo(DashboardAvailability.PermanentSkip));
    }

    [Test]
    public void ClassifyException_ArbitraryException_ReturnsPermanentSkip()
    {
        var ex = new InvalidOperationException("unexpected");

        var result = DashboardAvailabilityPolicy.ClassifyException(ex);

        Assert.That(result, Is.EqualTo(DashboardAvailability.PermanentSkip));
    }

    [Test]
    public void ClassifyException_OperationCanceledException_ReturnsPermanentSkip()
    {
        var ex = new OperationCanceledException();

        var result = DashboardAvailabilityPolicy.ClassifyException(ex);

        Assert.That(result, Is.EqualTo(DashboardAvailability.PermanentSkip));
    }
}
