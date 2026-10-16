using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Scans;

/// <summary>PasswordExpiring (section 10.14) with the constructed msDS-UserPasswordExpiryTimeComputed (maxPwdAge 42 days).</summary>
public class PasswordExpiryScannerTests
{
    // pwdLastSet 10 Sep 2026 08:00 UTC + 42 days = 22 Oct 2026 08:00 UTC: 8 days left on 14 Oct.
    private static readonly DateTimeOffset RecentPasswordSet = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PrivilegedAndServiceScope_SkipsOrdinaryUsers()
    {
        await using var scenario = await StartAsync(privileged: false);

        Assert.DoesNotContain(await scenario.ScanEventsAsync(), e => e.Kind == WatchEventKind.PasswordExpiring);
        Assert.Empty(await scenario.Store.GetUpcomingAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PrivilegedUser_IsInfoAt14DaysAndHighAt7Days()
    {
        await using var scenario = await StartAsync(privileged: true);

        var fourteen = Assert.Single(await scenario.ScanEventsAsync(), e => e.Kind == WatchEventKind.PasswordExpiring);
        scenario.Advance(TimeSpan.FromDays(1));
        var seven = Assert.Single(await scenario.ScanEventsAsync(), e => e.Kind == WatchEventKind.PasswordExpiring);

        Assert.Equal(14, fourteen.Expiry!.ThresholdDays);
        Assert.Equal(8, fourteen.Expiry.DaysLeft);
        Assert.Equal(ExpiryKind.Password, fourteen.Expiry.Kind);
        Assert.Contains(EventTags.Privileged, fourteen.Tags);
        Assert.Equal(Severity.Info, fourteen.Severity);
        Assert.Equal(7, seven.Expiry!.ThresholdDays);
        Assert.Equal(Severity.High, seven.Severity);
        var upcoming = Assert.Single(await scenario.Store.GetUpcomingAsync(TestContext.Current.CancellationToken));
        Assert.Contains(EventTags.Privileged, upcoming.Tags);
    }

    [Fact]
    public async Task ServiceAccountWithSpn_IsInScope()
    {
        await using var scenario = await StartAsync(privileged: false, spn: true);

        var expiring = Assert.Single(await scenario.ScanEventsAsync(), e => e.Kind == WatchEventKind.PasswordExpiring);

        Assert.Contains(EventTags.ServiceAccount, expiring.Tags);
    }

    [Fact]
    public async Task AllUsersScope_IncludesOrdinaryUsers_AndNoneScopeNothing()
    {
        await using var scenario = await StartAsync(privileged: false);
        scenario.Config.Update(c => c with { Scans = c.Scans with { PasswordExpiry = c.Scans.PasswordExpiry with { Scope = PasswordExpiryScope.AllUsers } } });

        Assert.Single(await scenario.ScanEventsAsync(), e => e.Kind == WatchEventKind.PasswordExpiring);

        scenario.Config.Update(c => c with { Scans = c.Scans with { PasswordExpiry = c.Scans.PasswordExpiry with { Scope = PasswordExpiryScope.None } } });
        scenario.Advance(TimeSpan.FromDays(1));
        Assert.DoesNotContain(await scenario.ScanEventsAsync(), e => e.Kind == WatchEventKind.PasswordExpiring);
        Assert.Empty(await scenario.Store.GetUpcomingAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MustChangeAndNeverExpiring_AreSkipped()
    {
        await using var scenario = await StartAsync(privileged: false);
        scenario.Config.Update(c => c with { Scans = c.Scans with { PasswordExpiry = c.Scans.PasswordExpiry with { Scope = PasswordExpiryScope.AllUsers } } });
        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.PwdLastSet, "0");
        scenario.Directory.SeedAttributes(scenario.Company.GrazynaDabrowska, new Dictionary<string, object?> { [Attr.PwdLastSet] = RecentPasswordSet });

        Assert.DoesNotContain(await scenario.ScanEventsAsync(), e => e.Kind == WatchEventKind.PasswordExpiring);
    }

    private static async Task<DetectionScenario> StartAsync(bool privileged, bool spn = false)
    {
        var scenario = new DetectionScenario();
        scenario.Directory.EmulateConstructedPasswordExpiry = true;
        scenario.Directory.SeedAttributes(scenario.Company.JanKowalski, new Dictionary<string, object?>
        {
            [Attr.PwdLastSet] = RecentPasswordSet,
            [Attr.ServicePrincipalName] = spn ? "HTTP/intranet.contoso.test" : null,
        });
        if (privileged)
        {
            await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, scenario.Company.JanKowalski);
        }

        await scenario.BaselineAsync();
        return scenario;
    }
}
