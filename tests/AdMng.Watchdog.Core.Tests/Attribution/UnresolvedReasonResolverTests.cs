using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Tests.Attribution.Fakes;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;
using Microsoft.Extensions.Time.Testing;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class UnresolvedReasonResolverTests
{
    private readonly FakeTimeProvider _time = new(Now.AddHours(-3));
    private readonly FakeSecurityEventStore _store = new();
    private readonly DcLogHealthTracker _tracker;
    private readonly UnresolvedReasonResolver _resolver;

    public UnresolvedReasonResolverTests()
    {
        _tracker = new DcLogHealthTracker(_time);
        _resolver = new UnresolvedReasonResolver(_store, _tracker, _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AttributionSearchPlan Plan() => new()
    {
        DomainControllers = [Dc1, Dc2],
        From = Now.AddMinutes(-20),
        To = Now.AddMinutes(-10),
        ReferenceTime = Now.AddMinutes(-15),
    };

    private Task<string> ResolveAsync(WatchEventKind kind = WatchEventKind.AccountDisabled)
    {
        AdvanceTo(Now);
        return _resolver.ResolveAsync(Create(kind), Plan(), Ct);
    }

    private void AdvanceTo(DateTimeOffset at)
    {
        if (at > _time.GetUtcNow())
        {
            _time.SetUtcNow(at);
        }
    }

    [Fact]
    public async Task AFailedReadSinceTheWindowBegan_IsDcUnreachable()
    {
        _store.Add(Account(4725, Now.AddDays(-1), Admin));
        AdvanceTo(Now.AddMinutes(-5));
        _tracker.RecordFailure(Dc2, "1722");

        Assert.Equal(AttributionReasons.DcUnreachable, await ResolveAsync());
    }

    [Fact]
    public async Task AnOldFailure_DoesNotCount()
    {
        _store.Add(Account(4725, Now.AddDays(-1), Admin));
        AdvanceTo(Now.AddHours(-2));
        _tracker.RecordFailure(Dc2, "1722");

        Assert.Equal(AttributionReasons.NoMatch, await ResolveAsync());
    }

    [Fact]
    public async Task OverwrittenRecords_AreLogOverwritten()
    {
        AdvanceTo(Now.AddMinutes(-1));
        _tracker.RecordOverwritten(Dc1);

        Assert.Equal(AttributionReasons.LogOverwritten, await ResolveAsync());
    }

    [Fact]
    public async Task NoAccountRecordsInSevenDays_IsAuditPolicyMissing()
    {
        _store.Add(Account(4725, Now.AddDays(-8), Admin));

        Assert.Equal(AttributionReasons.AuditPolicyMissing, await ResolveAsync());
    }

    [Fact]
    public async Task AccountRecordsSeen_IsNoMatch()
    {
        _store.Add(Account(4738, Now.AddDays(-2), Admin, computer: Dc2));

        Assert.Equal(AttributionReasons.NoMatch, await ResolveAsync());
    }

    [Fact]
    public async Task DirectoryOnlyKindsWithoutDirectoryRecords_AreSaclMissing()
    {
        _store.Add(Account(4738, Now.AddDays(-1), Admin));

        Assert.Equal(AttributionReasons.SaclMissing, await ResolveAsync(WatchEventKind.ObjectMoved));

        _store.Add(Parse(SecurityEventXml.DirectoryObject(5141, NextId(), Now.AddDays(-3), Dc2, Guid.NewGuid(), "CN=Stary,DC=contoso,DC=test", "user", Admin), Dc2));
        Assert.Equal(AttributionReasons.NoMatch, await ResolveAsync(WatchEventKind.ObjectMoved));
    }

    [Fact]
    public async Task TheDcsOwnComputerName_IsUsedForTheLookup()
    {
        _tracker.RecordComputerName(Dc1, "DC01.CONTOSO.TEST");
        _store.Add(Account(4725, Now.AddDays(-1), Admin, computer: "DC01.CONTOSO.TEST"));

        Assert.Equal(AttributionReasons.NoMatch, await ResolveAsync());
    }
}
