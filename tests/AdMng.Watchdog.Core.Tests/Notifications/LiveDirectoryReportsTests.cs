using AdMng.Core.ActiveDirectory;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications.Digest;
using AdMng.Watchdog.Core.Tests.Notifications.Fakes;
using AdMng.Watchdog.Testing.Stores;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class LiveDirectoryReportsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The directory was built long ago, so whenCreated of the seeded objects is old enough.</summary>
    private static readonly DateTimeOffset Created = new(2024, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeDirectoryBuilder _directory = new(timeProvider: new FakeTimeProvider(Created));
    private readonly InMemoryWatchdogStore _store = new(new FakeTimeProvider(Now));
    private readonly CapturingLogger<LiveDirectoryReports> _logger = new();

    [Fact]
    public async Task InactiveAccounts_FollowThe1017Filters()
    {
        var recent = Now.AddDays(-10);
        _directory.Session.SetValues(_directory.DomainControllerDn, Attr.LastLogonTimestamp, AdConvert.ToFileTime(recent).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _directory.Session.SetValues(_directory.AdministratorDn, Attr.LastLogonTimestamp, AdConvert.ToFileTime(Now.AddDays(-120)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var ou = _directory.AddOu("Księgowość");
        _directory.AddUser("Grażyna", "Kołodziejczyk", ou, Logon(Now.AddDays(-200)));
        _directory.AddUser("Bożena", "Żurawska", ou, Logon(Now.AddDays(-5)));
        _directory.AddUser("Mikołaj", "Nowicjusz", ou, new Dictionary<string, object?> { [Attr.WhenCreated] = Now.AddDays(-3) });
        _directory.AddUser("Wyłączony", "Użytkownik", ou, new Dictionary<string, object?>
        {
            [Attr.UserAccountControl] = (int)(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable),
        });
        _directory.AddComputer("WS-ŁÓDŹ-01", ou, Logon(Now.AddDays(-400)));
        _directory.AddComputer("WS-ŁÓDŹ-02", ou, Logon(Now.AddDays(-1)));
        await PrivilegedAsync(_directory.AdministratorDn);

        var result = await Reports().GetInactiveAccountsAsync(new InactiveAccountsScan(), Now, 10, Ct);

        Assert.NotNull(result);
        Assert.Equal((1, 1, 0), (result.Users, result.Computers, result.Privileged));   // Administrator: 120 days < PrivilegedDays (180)
        Assert.Equal(["WS-ŁÓDŹ-01", "Grażyna Kołodziejczyk"], result.Rows.Select(r => r.Account.Name));
        Assert.Equal(WatchObjectKind.Computer, result.Rows[0].Account.Kind);
        Assert.Equal(Now.AddDays(-200).ToUnixTimeSeconds(), result.Rows[1].LastLogon!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task InactivePrivilegedAccounts_UseTheirOwnThreshold_AndComeFirst()
    {
        _directory.Session.SetValues(_directory.DomainControllerDn, Attr.LastLogonTimestamp, AdConvert.ToFileTime(Now.AddDays(-1)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _directory.Session.SetValues(_directory.AdministratorDn, Attr.LastLogonTimestamp, AdConvert.ToFileTime(Now.AddDays(-365)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _directory.AddUser("Stanisław", "Zapomniany", attributes: Logon(Now.AddDays(-100)));
        await PrivilegedAsync(_directory.AdministratorDn);

        var result = await Reports().GetInactiveAccountsAsync(new InactiveAccountsScan(), Now, 1, Ct);

        Assert.NotNull(result);
        Assert.Equal((2, 1), (result.Users, result.Privileged));
        Assert.Equal("Administrator", Assert.Single(result.Rows).Account.Name);
    }

    [Fact]
    public async Task Hygiene_CountsTheRiskyFlagsOfEnabledAccounts()
    {
        _directory.AddUser("Hasło", "Opcjonalne", attributes: new Dictionary<string, object?>
        {
            [Attr.UserAccountControl] = (int)(UserAccountControl.NormalAccount | UserAccountControl.PasswordNotRequired),
        });
        _directory.AddUser("Wieczne", "Hasło", attributes: new Dictionary<string, object?>
        {
            [Attr.UserAccountControl] = (int)(UserAccountControl.NormalAccount | UserAccountControl.DontExpirePassword),
        });
        _directory.AddComputer("SRV-DELEGACJA", attributes: new Dictionary<string, object?>
        {
            [Attr.UserAccountControl] = (int)(UserAccountControl.WorkstationTrustAccount | UserAccountControl.TrustedForDelegation),
        });
        _directory.AddUser("Były", "Administrator", attributes: new Dictionary<string, object?> { [Attr.AdminCount] = 1 });
        await PrivilegedAsync(_directory.AdministratorDn);

        var counters = await Reports().GetHygieneAsync(Now, Ct);

        Assert.NotNull(counters);
        Assert.Equal(1, counters.PasswordNotRequired);                 // Guest has it too, but is disabled
        Assert.Equal(2, counters.PasswordNeverExpires);                // Administrator + Wieczne Hasło
        Assert.Equal(1, counters.UnconstrainedDelegation);             // DC01 is a domain controller
        Assert.Equal(1, counters.AdminCountOrphans);                   // krbtgt and the privileged Administrator do not count
        var expectedAge = (int)Math.Floor((Now - FakeDirectoryBuilder.DefaultPasswordLastSet).TotalDays);
        Assert.Equal((expectedAge, expectedAge), (counters.KrbtgtPasswordAgeDays, counters.AdministratorPasswordAgeDays));
    }

    [Fact]
    public async Task UnavailableDirectory_GivesNoData_AndLogsNoValues()
    {
        var reports = new LiveDirectoryReports(new FakeSessionProvider(null), _store, _logger);

        Assert.Null(await reports.GetHygieneAsync(Now, Ct));
        Assert.Null(await reports.GetInactiveAccountsAsync(new InactiveAccountsScan(), Now, 10, Ct));
        Assert.Equal(2, _logger.Entries.Count(e => e.Level == LogLevel.Warning));
        Assert.All(_logger.Entries, e => Assert.DoesNotContain("dc01.contoso.test", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServerFailures_AreReportedToTheSessionProvider()
    {
        var provider = new FakeSessionProvider(_directory.Session);
        _directory.Session.FailNext(FakeOperationKind.Search, null, AdMng.Core.Directory.DirectoryErrorKind.ServerUnavailable);

        Assert.Null(await new LiveDirectoryReports(provider, _store, _logger).GetHygieneAsync(Now, Ct));
        Assert.Equal(1, provider.FailureReports);
    }

    private LiveDirectoryReports Reports() => new(new FakeSessionProvider(_directory.Session), _store, _logger);

    private static Dictionary<string, object?> Logon(DateTimeOffset at) => new() { [Attr.LastLogonTimestamp] = at };

    private async Task PrivilegedAsync(string dn)
    {
        var guid = _directory.Session.Snapshot(dn)!.ObjectGuid!.Value;
        var group = _directory.Session.Snapshot(_directory.DomainAdminsDn)!.ObjectGuid!.Value;
        await _store.CommitAsync(new DetectionCommit
        {
            Privileges = new PrivilegeState
            {
                Principals = [new PrivilegedPrincipal { PrincipalKey = guid.ToString("N"), GroupGuid = group, Tier = PrivilegeTier.Critical }],
                ComputedAt = Now,
            },
        }, Ct);
    }
}
