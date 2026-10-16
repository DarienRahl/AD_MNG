using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.IntegrationTests.Infrastructure;

namespace AdMng.Watchdog.IntegrationTests.Detection;

/// <summary>
/// accountExpires set to five days ahead (spec 10.14, 22.4): the detection cycle reports AccountExpirationChanged, the
/// account expiry scanner announces AccountExpiring at the 7-day threshold (the class has its own engine, so this is
/// the scanner's first run).
/// </summary>
public sealed class ExpiryScanTests(ReadOnlyEngineFixture engine, LabFixture lab) : IClassFixture<ReadOnlyEngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EngineHost Host => engine.Host;

    [Fact]
    public async Task AccountExpiringInFiveDays_IsAccountExpirationChanged_ThenAccountExpiringAtSevenDays()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Wygasające", Ct);
        var user = await lab.Writer.CreateUserAsync(ou, "Łukasz", "Wiśniewski", TestNames.Password(), cancellationToken: Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        var expires = DateTimeOffset.UtcNow.AddDays(5);
        await lab.Writer.ReplaceAsync(user.Dn, Attr.AccountExpires, AdConvert.ToFileTime(expires).ToString(CultureInfo.InvariantCulture), Ct);
        await Host.RunDetectionAsync(Ct);
        await Host.RunScansAsync(Ct);

        var events = await Host.EventsAsync(ou, mark, Ct);
        EventAssert.Single(events, WatchEventKind.AccountExpirationChanged, user.Dn);
        var expiring = EventAssert.Single(events, WatchEventKind.AccountExpiring, user.Dn);
        Assert.Equal(DetectionSource.ExpiryScan, expiring.Source);
        var expiry = Assert.IsType<ExpiryInfo>(expiring.Expiry);
        Assert.Equal(ExpiryKind.Account, expiry.Kind);
        Assert.Equal(7, expiry.ThresholdDays);
        Assert.Equal(5, expiry.DaysLeft);

        // The scanner remembers the notice: the next run does not announce the same threshold again.
        mark = await Host.MarkAsync(Ct);
        Host.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        await Host.RunScansAsync(Ct);
        EventAssert.None(await Host.EventsAsync(ou, mark, Ct), user.Dn);
    }
}
