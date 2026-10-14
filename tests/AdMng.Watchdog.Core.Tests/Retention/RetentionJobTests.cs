using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Retention;
using AdMng.Watchdog.Core.Tests.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using static AdMng.Watchdog.Core.Tests.Notifications.NotificationHarness;

namespace AdMng.Watchdog.Core.Tests.Retention;

public sealed class RetentionJobTests
{
    /// <summary>Thursday 15 October 2026, 03:15 in Warsaw.</summary>
    private static readonly DateTimeOffset NextMaintenance = new(2026, 10, 15, 1, 15, 0, TimeSpan.Zero);

    private const long Mb = 1024 * 1024;

    private readonly NotificationHarness _harness = new(ConfigWith() with { Retention = new RetentionSettings { MaxDatabaseMb = 100, EventsDays = 30 } });
    private readonly RetentionJob _job;

    public RetentionJobTests()
    {
        _job = new RetentionJob(_harness.Store, _harness.Store, _harness.Health, _harness.Config, _harness.Calendars, _harness.Signals, _harness.Clock,
            NullLogger<RetentionJob>.Instance);
    }

    [Fact]
    public void Job_IsRetentionCheckingHourly()
    {
        Assert.Equal(("retention", TimeSpan.FromHours(1)), (_job.Name, _job.DefaultInterval));
        Assert.Equal([WatchdogSignal.ConfigChanged], _job.WakeSignals);
    }

    [Fact]
    public async Task MissedMaintenance_RunsAtStartUp_ThenDailyAtMaintenanceTime()
    {
        var old = await _harness.StoreAsync(Event(detectedAt: Now.AddDays(-40)));
        var recent = await _harness.StoreAsync(Event(detectedAt: Now.AddDays(-2)));

        var result = await _job.RunOnceAsync(Ct);

        Assert.Equal(1, _harness.Store.BackupCount);
        Assert.Null(await _harness.Store.GetAsync(old.Id, Ct));
        Assert.NotNull(await _harness.Store.GetAsync(recent.Id, Ct));
        Assert.Equal(NextMaintenance, _job.NextMaintenanceAt);
        Assert.Equal(TimeSpan.FromHours(1), result.NextRunIn);
        Assert.Equal(HealthState.Ok, _harness.Health.GetCheck(RetentionJob.MaintenanceCode)!.State);

        _harness.Clock.Advance(TimeSpan.FromHours(1));
        await _job.RunOnceAsync(Ct);
        Assert.Equal(1, _harness.Store.BackupCount);

        _harness.Clock.SetUtcNow(NextMaintenance);
        await _job.RunOnceAsync(Ct);
        Assert.Equal(2, _harness.Store.BackupCount);
        Assert.Equal(NextMaintenance.AddDays(1), _job.NextMaintenanceAt);
    }

    [Fact]
    public async Task BackupSinceThePreviousMaintenanceTime_IsNotRepeatedAtStartUp_ButAnOlderOneIs()
    {
        await _harness.Store.BackupAsync(Ct);            // 10:00 local, after today's 03:15

        var result = await _job.RunOnceAsync(Ct);

        Assert.Equal(1, _harness.Store.BackupCount);
        Assert.Equal(NextMaintenance, _job.NextMaintenanceAt);
        Assert.False(result.Failed);

        _harness.Clock.Advance(TimeSpan.FromDays(1));    // the service was down over the next maintenance
        var restarted = new RetentionJob(_harness.Store, _harness.Store, _harness.Health, _harness.Config, _harness.Calendars, _harness.Signals, _harness.Clock,
            NullLogger<RetentionJob>.Instance);
        await restarted.RunOnceAsync(Ct);
        Assert.Equal(2, _harness.Store.BackupCount);
    }

    [Fact]
    public async Task OverTheCap_PurgesWithinTheHour_WithoutABackup()
    {
        await _harness.Store.BackupAsync(Ct);
        await _job.RunOnceAsync(Ct);
        var old = await _harness.StoreAsync(Event(detectedAt: Now.AddDays(-40)));
        _harness.Store.SimulatedDatabaseBytes = 120 * Mb;

        await _job.RunOnceAsync(Ct);

        Assert.Null(await _harness.Store.GetAsync(old.Id, Ct));
        Assert.Equal(1, _harness.Store.BackupCount);
        Assert.Equal(HealthState.Error, _harness.Health.GetCheck(StorageHealth.SizeCode)!.State);
    }

    [Fact]
    public async Task EightyPercentOfTheCap_RaisesOneHealthStorageEventPerProblem()
    {
        using var stored = _harness.Signals.Subscribe([WatchdogSignal.EventsStored]);
        await _harness.Store.BackupAsync(Ct);
        _harness.Store.SimulatedDatabaseBytes = 85 * Mb;

        await _job.RunOnceAsync(Ct);
        await _job.RunOnceAsync(Ct);

        var health = Assert.Single(await HealthEventsAsync());
        Assert.Equal((WatchEventKind.HealthStorage, Severity.High), (health.Kind, health.Severity));
        Assert.Equal(["storage.size", "85", "100", "1048576"], health.Changes.Select(c => c.NewValues.Single()));
        Assert.Equal(HealthState.Warning, _harness.Health.GetCheck(StorageHealth.SizeCode)!.State);
        Assert.True(await stored.WaitAsync(TimeSpan.Zero, Ct));

        _harness.Store.SimulatedDatabaseBytes = 10 * Mb;
        await _job.RunOnceAsync(Ct);
        Assert.Equal(HealthState.Ok, _harness.Health.GetCheck(StorageHealth.SizeCode)!.State);

        _harness.Store.SimulatedFreeBytes = 512 * Mb;
        await _job.RunOnceAsync(Ct);
        Assert.Equal(2, (await HealthEventsAsync()).Count);
        Assert.Equal(HealthState.Error, _harness.Health.GetCheck(StorageHealth.FreeCode)!.State);
        Assert.Equal("storage.free", (await HealthEventsAsync())[1].Changes[0].NewValues.Single());
    }

    [Fact]
    public async Task ChangedMaintenanceTime_MovesTheSchedule()
    {
        await _harness.Store.BackupAsync(Ct);
        await _job.RunOnceAsync(Ct);
        _harness.Config.Set(_harness.Current with { Retention = _harness.Current.Retention with { MaintenanceTime = new TimeOnly(23, 0) } });

        var result = await _job.RunOnceAsync(Ct);

        Assert.Equal(new DateTimeOffset(2026, 10, 14, 21, 0, 0, TimeSpan.Zero), _job.NextMaintenanceAt);
        Assert.Equal(TimeSpan.FromHours(1), result.NextRunIn);
        Assert.Equal(1, _harness.Store.BackupCount);
    }

    private async Task<List<WatchEvent>> HealthEventsAsync()
    {
        var events = new List<WatchEvent>();
        await foreach (var e in _harness.Store.GetRangeAsync(Now.AddDays(-1), Now.AddDays(3), Ct))
        {
            if (e.Kind == WatchEventKind.HealthStorage)
            {
                events.Add(e);
            }
        }

        return events;
    }
}
