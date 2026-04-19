using BackupsterAgent.Services.Dashboard;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupsterAgent.Tests.Services;

[TestFixture]
public sealed class DashboardAuthGuardTests
{
    [Test]
    public void OnUnauthorized_FirstCall_StopsApplication()
    {
        var lifetime = new FakeLifetime();
        var guard = new DashboardAuthGuard(lifetime);

        guard.OnUnauthorized("Channel.Test", NullLogger.Instance);

        Assert.That(lifetime.StopCalls, Is.EqualTo(1));
    }

    [Test]
    public void OnUnauthorized_MultipleCalls_StopsOnlyOnce()
    {
        var lifetime = new FakeLifetime();
        var guard = new DashboardAuthGuard(lifetime);

        guard.OnUnauthorized("A", NullLogger.Instance);
        guard.OnUnauthorized("B", NullLogger.Instance);
        guard.OnUnauthorized("C", NullLogger.Instance);

        Assert.That(lifetime.StopCalls, Is.EqualTo(1),
            "repeated 401s from different channels must not spam StopApplication");
    }

    [Test]
    public void OnUnauthorized_ConcurrentCalls_StopsOnlyOnce()
    {
        var lifetime = new FakeLifetime();
        var guard = new DashboardAuthGuard(lifetime);

        Parallel.For(0, 32, _ => guard.OnUnauthorized("parallel", NullLogger.Instance));

        Assert.That(lifetime.StopCalls, Is.EqualTo(1));
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public int StopCalls { get; private set; }
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() => StopCalls++;
    }
}
