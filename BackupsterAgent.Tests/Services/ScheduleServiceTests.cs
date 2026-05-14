using System.Text.Json;
using BackupsterAgent.Configuration;
using BackupsterAgent.Contracts;
using BackupsterAgent.Enums;
using BackupsterAgent.Services.Common.State;
using BackupsterAgent.Services.Dashboard;
using BackupsterAgent.Services.Dashboard.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupsterAgent.Tests.Services;

[TestFixture]
public sealed class ScheduleServiceTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"schedule-svc-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Test]
    public async Task NoCache_ReturnsEmpty()
    {
        var svc = CreateService(schedule: null);

        var entries = await svc.GetDueSchedulesAsync("payments", CancellationToken.None);

        Assert.That(entries, Is.Empty);
    }

    [Test]
    public async Task DefaultActive_NoOverrides_ReturnsEmpty()
    {
        var svc = CreateService(new ScheduleDto
        {
            CronExpression = "0 2 * * *",
            IsActive = true,
        });

        var entries = await svc.GetDueSchedulesAsync("payments", CancellationToken.None);

        Assert.That(entries, Is.Empty,
            "default should be ignored by agent now — only per-(db,mode) overrides are honored");
    }

    [Test]
    public async Task LogicalOverrideActive_ReturnsLogicalOnly()
    {
        var svc = CreateService(new ScheduleDto
        {
            CronExpression = string.Empty,
            IsActive = false,
            Overrides =
            [
                new ScheduleOverrideDto
                {
                    DatabaseName = "payments",
                    CronExpression = "0 3 * * *",
                    IsActive = true,
                    BackupMode = BackupMode.Logical,
                },
            ],
        });

        var entries = await svc.GetDueSchedulesAsync("payments", CancellationToken.None);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Mode, Is.EqualTo(BackupMode.Logical));
        Assert.That(entries[0].NextRun, Is.EqualTo(NextOccurrence("0 3 * * *")));
    }

    [Test]
    public async Task PhysicalOverrideActive_ReturnsPhysicalOnly()
    {
        var svc = CreateService(new ScheduleDto
        {
            CronExpression = string.Empty,
            IsActive = false,
            Overrides =
            [
                new ScheduleOverrideDto
                {
                    DatabaseName = "payments",
                    CronExpression = "0 4 * * *",
                    IsActive = true,
                    BackupMode = BackupMode.Physical,
                },
            ],
        });

        var entries = await svc.GetDueSchedulesAsync("payments", CancellationToken.None);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Mode, Is.EqualTo(BackupMode.Physical));
        Assert.That(entries[0].NextRun, Is.EqualTo(NextOccurrence("0 4 * * *")));
    }

    [Test]
    public async Task BothOverridesActive_ReturnsTwoIndependentEntries()
    {
        var logicalRun = NextOccurrence("0 3 * * *");
        var physicalRun = NextOccurrence("0 4 * * *");

        var svc = CreateService(new ScheduleDto
        {
            CronExpression = string.Empty,
            IsActive = false,
            Overrides =
            [
                new ScheduleOverrideDto
                {
                    DatabaseName = "payments",
                    CronExpression = "0 3 * * *",
                    IsActive = true,
                    BackupMode = BackupMode.Logical,
                },
                new ScheduleOverrideDto
                {
                    DatabaseName = "payments",
                    CronExpression = "0 4 * * *",
                    IsActive = true,
                    BackupMode = BackupMode.Physical,
                },
            ],
        });

        var entries = await svc.GetDueSchedulesAsync("payments", CancellationToken.None);

        Assert.That(entries, Has.Count.EqualTo(2));
        var byMode = entries.ToDictionary(e => e.Mode, e => e.NextRun);
        Assert.Multiple(() =>
        {
            Assert.That(byMode[BackupMode.Logical], Is.EqualTo(logicalRun));
            Assert.That(byMode[BackupMode.Physical], Is.EqualTo(physicalRun));
        });
    }

    [Test]
    public async Task InactiveOverride_Skipped()
    {
        var svc = CreateService(new ScheduleDto
        {
            CronExpression = string.Empty,
            IsActive = false,
            Overrides =
            [
                new ScheduleOverrideDto
                {
                    DatabaseName = "payments",
                    CronExpression = "0 3 * * *",
                    IsActive = false,
                    BackupMode = BackupMode.Logical,
                },
                new ScheduleOverrideDto
                {
                    DatabaseName = "payments",
                    CronExpression = "0 4 * * *",
                    IsActive = true,
                    BackupMode = BackupMode.Physical,
                },
            ],
        });

        var entries = await svc.GetDueSchedulesAsync("payments", CancellationToken.None);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Mode, Is.EqualTo(BackupMode.Physical));
    }

    [Test]
    public async Task AllOverridesInactive_ReturnsEmpty()
    {
        var svc = CreateService(new ScheduleDto
        {
            CronExpression = "0 2 * * *",
            IsActive = true,
            Overrides =
            [
                new ScheduleOverrideDto
                {
                    DatabaseName = "payments",
                    CronExpression = "0 3 * * *",
                    IsActive = false,
                    BackupMode = BackupMode.Logical,
                },
            ],
        });

        var entries = await svc.GetDueSchedulesAsync("payments", CancellationToken.None);

        Assert.That(entries, Is.Empty);
    }

    [Test]
    public async Task OverrideForOtherDatabase_DoesNotLeak()
    {
        var svc = CreateService(new ScheduleDto
        {
            CronExpression = string.Empty,
            IsActive = false,
            Overrides =
            [
                new ScheduleOverrideDto
                {
                    DatabaseName = "orders",
                    CronExpression = "0 5 * * *",
                    IsActive = true,
                    BackupMode = BackupMode.Physical,
                },
            ],
        });

        var entries = await svc.GetDueSchedulesAsync("payments", CancellationToken.None);

        Assert.That(entries, Is.Empty);
    }

    [Test]
    public async Task TwoDatabasesIndependent_EachQueriedSeparately()
    {
        var svc = CreateService(new ScheduleDto
        {
            CronExpression = string.Empty,
            IsActive = false,
            Overrides =
            [
                new ScheduleOverrideDto
                {
                    DatabaseName = "payments",
                    CronExpression = "0 3 * * *",
                    IsActive = true,
                    BackupMode = BackupMode.Logical,
                },
                new ScheduleOverrideDto
                {
                    DatabaseName = "payments",
                    CronExpression = "0 4 * * *",
                    IsActive = true,
                    BackupMode = BackupMode.Physical,
                },
                new ScheduleOverrideDto
                {
                    DatabaseName = "orders",
                    CronExpression = "0 5 * * *",
                    IsActive = true,
                    BackupMode = BackupMode.Logical,
                },
            ],
        });

        var payments = await svc.GetDueSchedulesAsync("payments", CancellationToken.None);
        var orders = await svc.GetDueSchedulesAsync("orders", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(payments, Has.Count.EqualTo(2));
            Assert.That(orders, Has.Count.EqualTo(1));
            Assert.That(orders[0].Mode, Is.EqualTo(BackupMode.Logical));
            Assert.That(orders[0].NextRun, Is.EqualTo(NextOccurrence("0 5 * * *")));
        });
    }

    private ScheduleService CreateService(ScheduleDto? schedule)
    {
        var scheduleFile = Path.Combine(_root, "schedule.json");
        if (schedule is not null)
        {
            File.WriteAllText(scheduleFile,
                JsonSerializer.Serialize(schedule, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        var store = new ScheduleStore(scheduleFile, NullLogger<ScheduleStore>.Instance);
        var http = new HttpClient();
        var settings = Options.Create(new AgentSettings { Token = string.Empty, DashboardUrl = string.Empty });
        var authGuard = new FakeAuthGuard();

        return new ScheduleService(http, store, settings, authGuard, NullLogger<ScheduleService>.Instance);
    }

    private static DateTime NextOccurrence(string cron)
    {
        var parsed = NCrontab.CrontabSchedule.Parse(cron);
        return parsed.GetNextOccurrence(DateTime.UtcNow - TimeSpan.FromMinutes(5));
    }

    private sealed class FakeAuthGuard : IDashboardAuthGuard
    {
        public void OnUnauthorized(string channel, ILogger logger) { }
    }
}
