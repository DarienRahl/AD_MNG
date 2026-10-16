using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Scans;

/// <summary>AccountExpiring / AccountExpired (section 10.14). The scenario starts on Wednesday 14 Oct 2026, 10:00 in Warsaw.</summary>
public class AccountExpiryScannerTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    [Fact]
    public async Task Thresholds_FireOnceEachInCalendarDaysToTheLastValidDay_ThenExpiredOnce()
    {
        await using var scenario = await StartAsync(new DateOnly(2026, 10, 28));

        var fourteen = Expiring(await scenario.ScanEventsAsync());
        scenario.Advance(TimeSpan.FromHours(1));
        Assert.Empty(Expiring(await scenario.ScanEventsAsync()));
        AdvanceTo(scenario, new DateOnly(2026, 10, 21));
        var seven = Expiring(await scenario.ScanEventsAsync());
        AdvanceTo(scenario, new DateOnly(2026, 10, 27));
        var one = Expiring(await scenario.ScanEventsAsync());
        AdvanceTo(scenario, new DateOnly(2026, 10, 29));
        var expired = await scenario.ScanEventsAsync();
        scenario.Advance(TimeSpan.FromHours(1));
        var again = await scenario.ScanEventsAsync();

        Assert.Equal((14, 14), Single(fourteen));
        Assert.Equal((7, 7), Single(seven));
        Assert.Equal((1, 1), Single(one));
        var gone = Assert.Single(expired, e => e.Kind == WatchEventKind.AccountExpired);
        Assert.Equal(new DateOnly(2026, 10, 28), gone.Expiry!.LastValidDay);
        Assert.Equal(AttributionStatus.NotApplicable, gone.Attribution.Status);
        Assert.Equal(DetectionSource.ExpiryScan, gone.Source);
        Assert.DoesNotContain(again, e => e.Kind == WatchEventKind.AccountExpired);
    }

    [Fact]
    public async Task SundayExpiry_IsAnnouncedOnFriday()
    {
        await using var scenario = await StartAsync(new DateOnly(2026, 10, 25));
        await scenario.ScanEventsAsync();

        AdvanceTo(scenario, new DateOnly(2026, 10, 22));
        Assert.DoesNotContain(await scenario.ScanEventsAsync(), e => e.Expiry?.ThresholdDays == 1);
        AdvanceTo(scenario, new DateOnly(2026, 10, 23), hour: 0, minute: 30);
        var friday = Expiring(await scenario.ScanEventsAsync());

        Assert.Equal((1, 2), Single(friday));
    }

    [Fact]
    public async Task HolidayStretch_MovesTheThresholdBeforeChristmas()
    {
        await using var scenario = await StartAsync(new DateOnly(2026, 12, 27), configuration: c => c with
        {
            Scans = c.Scans with { AccountExpiry = c.Scans.AccountExpiry with { ThresholdsDays = [1] } },
            BusinessHours = c.BusinessHours with { ExtraHolidays = [new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 25), new DateOnly(2026, 12, 26)] },
        });

        AdvanceTo(scenario, new DateOnly(2026, 12, 22));
        Assert.Empty(Expiring(await scenario.ScanEventsAsync()));
        AdvanceTo(scenario, new DateOnly(2026, 12, 23));
        var announced = Expiring(await scenario.ScanEventsAsync());

        Assert.Equal((1, 4), Single(announced));
    }

    [Fact]
    public async Task Downtime_RecordsLargerThresholdsSilently()
    {
        await using var scenario = await StartAsync(new DateOnly(2026, 10, 16));

        var first = Expiring(await scenario.ScanEventsAsync());
        AdvanceTo(scenario, new DateOnly(2026, 10, 15));
        var next = Expiring(await scenario.ScanEventsAsync());

        Assert.Equal((7, 2), Single(first));
        Assert.Equal((1, 1), Single(next));
    }

    [Fact]
    public async Task ChangedExpiry_RearmsTheThresholds()
    {
        await using var scenario = await StartAsync(new DateOnly(2026, 10, 28));
        Assert.Single(Expiring(await scenario.ScanEventsAsync()));

        await scenario.ReplaceAsync(scenario.Company.LukaszWisniewski, Attr.AccountExpires, EndOfDay(new DateOnly(2026, 10, 27)));
        await scenario.CycleEventsAsync();
        scenario.Advance(TimeSpan.FromHours(1));
        var rearmed = Expiring(await scenario.ScanEventsAsync());

        Assert.Equal((14, 13), Single(rearmed));
    }

    [Fact]
    public async Task DisabledAccounts_AreSkippedUnlessIncluded()
    {
        await using var scenario = await StartAsync(new DateOnly(2026, 10, 28), user: s => s.Company.ZbigniewKazmierczak);
        Assert.Empty(Expiring(await scenario.ScanEventsAsync()));

        scenario.Config.Update(c => c with { Scans = c.Scans with { AccountExpiry = c.Scans.AccountExpiry with { IncludeDisabled = true, Interval = TimeSpan.Zero } } });

        Assert.Single(Expiring(await scenario.ScanEventsAsync()));
    }

    [Fact]
    public async Task UpcomingExpiries_AreReplacedForTheDigest()
    {
        await using var scenario = await StartAsync(new DateOnly(2026, 10, 20));

        await scenario.ScanEventsAsync();

        var upcoming = Assert.Single(await scenario.Store.GetUpcomingAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ExpiryKind.Account, upcoming.Kind);
        Assert.Equal(scenario.Company.LukaszWisniewski, upcoming.Object.Dn);
    }

    private static async Task<DetectionScenario> StartAsync(DateOnly endOfDay, Func<DetectionScenario, string>? user = null,
        Func<WatchdogConfiguration, WatchdogConfiguration>? configuration = null)
    {
        var scenario = new DetectionScenario();
        if (configuration is not null)
        {
            scenario.Config.Update(configuration);
        }

        var dn = user?.Invoke(scenario) ?? scenario.Company.LukaszWisniewski;
        scenario.Directory.SeedAttributes(dn, new Dictionary<string, object?> { [Attr.AccountExpires] = EndOfDay(endOfDay) });
        await scenario.BaselineAsync();
        return scenario;
    }

    /// <summary>What ADUC stores for "End of: <paramref name="day"/>": the local midnight that starts the next day.</summary>
    private static string EndOfDay(DateOnly day)
    {
        var midnight = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var moment = new DateTimeOffset(midnight, Warsaw.GetUtcOffset(midnight));
        return AdConvert.ToFileTime(moment).ToString(CultureInfo.InvariantCulture);
    }

    private static void AdvanceTo(DetectionScenario scenario, DateOnly day, int hour = 8, int minute = 0)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute));
        var target = new DateTimeOffset(local, Warsaw.GetUtcOffset(local));
        scenario.Advance(target - scenario.Time.GetUtcNow());
    }

    private static List<WatchEvent> Expiring(IReadOnlyList<WatchEvent> events) => [.. events.Where(e => e.Kind == WatchEventKind.AccountExpiring)];

    private static (int Threshold, int DaysLeft) Single(List<WatchEvent> events)
    {
        var expiring = Assert.Single(events);
        return (expiring.Expiry!.ThresholdDays, expiring.Expiry.DaysLeft);
    }
}
