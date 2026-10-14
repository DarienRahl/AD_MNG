using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Scans;

/// <summary>LockoutExpired (section 10.13).</summary>
public class LockoutMonitorTests
{
    [Fact]
    public async Task ExpiredLockout_IsReportedOnceWhenEnabled()
    {
        await using var scenario = await StartAsync(enabled: true);
        scenario.Directory.SimulateLockout(scenario.Company.PawelLewandowski);
        Assert.DoesNotContain(await scenario.ScanEventsAsync(), e => e.Kind == WatchEventKind.LockoutExpired);

        scenario.Advance(TimeSpan.FromMinutes(31));
        var expired = Assert.Single(await scenario.ScanEventsAsync(), e => e.Kind == WatchEventKind.LockoutExpired);
        scenario.Advance(TimeSpan.FromMinutes(5));
        var again = await scenario.ScanEventsAsync();

        Assert.Equal(scenario.Company.PawelLewandowski, expired.Object.Dn);
        Assert.Equal(DetectionSource.LockoutMonitor, expired.Source);
        Assert.Equal(DetectionScenario.Start.AddMinutes(30), expired.Lockout!.LockedUntil);
        Assert.Equal(Severity.Info, expired.Severity);
        Assert.DoesNotContain(again, e => e.Kind == WatchEventKind.LockoutExpired);
    }

    [Fact]
    public async Task UnlockedByAnAdmin_IsNoLockoutExpired()
    {
        await using var scenario = await StartAsync(enabled: true);
        scenario.Directory.SimulateLockout(scenario.Company.PawelLewandowski);
        await scenario.ScanEventsAsync();

        await scenario.ReplaceAsync(scenario.Company.PawelLewandowski, Attr.LockoutTime, "0");
        scenario.Advance(TimeSpan.FromMinutes(31));

        Assert.DoesNotContain(await scenario.ScanEventsAsync(), e => e.Kind == WatchEventKind.LockoutExpired);
    }

    [Fact]
    public async Task LockedBeforeTheFirstCheck_AndExpiredInBetween_IsNoEvent()
    {
        await using var scenario = await StartAsync(enabled: true);
        scenario.Directory.SimulateLockout(scenario.Company.PawelLewandowski);
        scenario.Advance(TimeSpan.FromMinutes(31));

        Assert.DoesNotContain(await scenario.ScanEventsAsync(), e => e.Kind == WatchEventKind.LockoutExpired);
    }

    [Fact]
    public async Task NotRecordedByDefault()
    {
        await using var scenario = await StartAsync(enabled: false);
        scenario.Directory.SimulateLockout(scenario.Company.PawelLewandowski);
        await scenario.ScanEventsAsync();

        scenario.Advance(TimeSpan.FromMinutes(31));

        Assert.DoesNotContain(await scenario.ScanEventsAsync(), e => e.Kind == WatchEventKind.LockoutExpired);
    }

    private static async Task<DetectionScenario> StartAsync(bool enabled)
    {
        var scenario = new DetectionScenario(configuration: enabled
            ? new WatchdogConfiguration { Directory = new DirectorySettings { EnabledOptionalKinds = [WatchEventKind.LockoutExpired] } }
            : null);
        scenario.Directory.LockoutDuration = TimeSpan.FromMinutes(30);
        await scenario.BaselineAsync();
        return scenario;
    }
}
