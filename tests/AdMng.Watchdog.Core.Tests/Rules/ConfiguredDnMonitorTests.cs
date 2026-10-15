using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Core.Tests.Attribution.Fakes;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class ConfiguredDnMonitorTests
{
    private readonly FakeDetectionStore _snapshot = new();
    private readonly FakeHealthRegistry _health = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly ConfiguredDnMonitor _monitor;

    public ConfiguredDnMonitorTests() => _monitor = new ConfiguredDnMonitor(_snapshot, _health, _time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Snapshot(string dn, WatchObjectKind kind) =>
        _snapshot.Add(new SnapshotObject { ObjectGuid = Guid.NewGuid(), Dn = dn, Kind = kind });

    private static WatchdogConfiguration Configuration()
    {
        var defaults = new DefaultConfigurationFactory().Create();
        return WatchdogConfigProvider.Effective(defaults with
        {
            PrivilegedGroups = defaults.PrivilegedGroups with
            {
                AdditionalCriticalDns = ["CN=Administratorzy SQL,OU=Grupy,{domain}"],
                WatchListDns = ["CN=Księgowość,OU=Grupy,{domain}"],
            },
            Rules =
            [
                .. defaults.Rules,
                new RuleSettings
                {
                    Id = "warszawa",
                    Name = "Oddział Warszawa",
                    Scope = new RuleScope { IncludeSubtrees = ["OU=Warszawa,{domain}"], ExcludeSubtrees = ["OU=Testy,OU=Warszawa,{domain}"] },
                },
            ],
        });
    }

    private void SnapshotEverything()
    {
        Snapshot(DomainDn, WatchObjectKind.Domain);
        Snapshot("OU=Grupy," + DomainDn, WatchObjectKind.OrganizationalUnit);
        Snapshot("CN=Administratorzy SQL,OU=Grupy," + DomainDn, WatchObjectKind.Group);
        Snapshot("CN=Księgowość,OU=Grupy," + DomainDn, WatchObjectKind.Group);
        Snapshot("OU=Warszawa," + DomainDn, WatchObjectKind.OrganizationalUnit);
        Snapshot("OU=Testy,OU=Warszawa," + DomainDn, WatchObjectKind.OrganizationalUnit);
        Snapshot("OU=Domain Controllers," + DomainDn, WatchObjectKind.OrganizationalUnit);
    }

    [Fact]
    public async Task EveryConfiguredDnInTheSnapshot_IsOk()
    {
        SnapshotEverything();

        await _monitor.CheckAsync(Configuration(), Tokens, Ct);

        Assert.Equal(HealthState.Ok, _health.Find(ConfiguredDnMonitor.CheckCode)!.State);
    }

    [Fact]
    public async Task ARenamedOuOrMovedGroup_IsAWarningNamingTheConfiguredDn()
    {
        // attribution-rules-13: OU=Warszawa renamed to OU=Warszawa-Centrum – the rule silently matched nothing.
        Snapshot(DomainDn, WatchObjectKind.Domain);
        Snapshot("OU=Warszawa-Centrum," + DomainDn, WatchObjectKind.OrganizationalUnit);
        Snapshot("CN=Księgowość,OU=Grupy," + DomainDn, WatchObjectKind.Group);
        Snapshot("OU=Domain Controllers," + DomainDn, WatchObjectKind.OrganizationalUnit);

        await _monitor.CheckAsync(Configuration(), Tokens, Ct);

        var check = _health.Find(ConfiguredDnMonitor.CheckCode)!;
        Assert.Equal(HealthState.Warning, check.State);
        Assert.Contains("OU=Warszawa,{domain}", check.Args);
        Assert.Contains("CN=Administratorzy SQL,OU=Grupy,{domain}", check.Args);
        Assert.DoesNotContain("CN=Księgowość,OU=Grupy,{domain}", check.Args);
        Assert.DoesNotContain(check.Args, a => a.Contains("AdminSDHolder", StringComparison.Ordinal));   // CN=System is not snapshotted
    }

    [Fact]
    public async Task BeforeTheBaseline_NothingIsReported()
    {
        await _monitor.CheckAsync(Configuration(), Tokens, Ct);

        Assert.Null(_health.Find(ConfiguredDnMonitor.CheckCode));
    }

    [Fact]
    public async Task TheCheck_RunsAgainAfterTheIntervalOrForAnotherConfiguration()
    {
        SnapshotEverything();
        var configuration = Configuration();
        await _monitor.CheckAsync(configuration, Tokens, Ct);
        var first = _health.Find(ConfiguredDnMonitor.CheckCode)!.CheckedAt;

        _time.Advance(TimeSpan.FromMinutes(1));
        await _monitor.CheckAsync(configuration, Tokens, Ct);
        Assert.Equal(first, _health.Find(ConfiguredDnMonitor.CheckCode)!.CheckedAt);

        _time.Advance(ConfiguredDnMonitor.Interval);
        await _monitor.CheckAsync(configuration, Tokens, Ct);
        Assert.NotEqual(first, _health.Find(ConfiguredDnMonitor.CheckCode)!.CheckedAt);
    }
}
