using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Tests.Attribution.Fakes;
using AdMng.Watchdog.Testing.Attribution;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;
using Microsoft.Extensions.Time.Testing;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public sealed class LockoutSourceResolverTests : IDisposable
{
    private const string Pdc = "dc02.contoso.test";
    private static readonly DateTimeOffset LockedAt = Now.AddMinutes(-3);
    private static readonly AttributionSettings Settings = new();

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeSecurityEventStore _store = new();
    private readonly FakeSecurityEventSource _source = new();
    private readonly FakeDomainControllers _dcs = new(Dc1, Dc2) { Pdc = Pdc };
    private readonly SourceQueryGate _gate;
    private readonly LockoutSourceResolver _resolver;

    public LockoutSourceResolverTests()
    {
        _gate = new SourceQueryGate(new DcLogHealthTracker(_time), _time);
        _resolver = new LockoutSourceResolver(_store, _source, _gate, _dcs, new FixedLockoutPolicy(TimeSpan.FromMinutes(30)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _gate.Dispose();

    private static WatchEvent Lockout() => Create(WatchEventKind.AccountLockedOut) with
    {
        Lockout = new LockoutInfo { LockedAt = LockedAt, LockedUntil = LockedAt.AddMinutes(30) },
        Attribution = EventAttribution.NotApplicable,
    };

    private void Add4740(string? caller) => _store.Add(Parse(SecurityEventXml.Create(4740, NextId(), LockedAt, Dc1,
        [("TargetUserName", "lwisniewski"), ("TargetDomainName", caller), ("TargetSid", UserSid), .. LocalSystem])));

    private void AddFailure(string host, Func<long, string> create) => _source.Add(host, create);

    [Fact]
    public async Task AWorkstationCaller_IsTheSourceWithoutLookups()
    {
        Add4740("PC-KRAKOW-07");

        var resolution = await _resolver.ResolveAsync(Lockout(), Settings, Ct);

        Assert.Equal("PC-KRAKOW-07", resolution.Lockout.CallerComputer);
        var caller = Assert.Single(resolution.Lockout.Sources);
        Assert.Equal(new LockoutSource(null, "PC-KRAKOW-07", 1, 4740), caller);
        Assert.Equal(LockedAt.AddMinutes(30), resolution.Lockout.LockedUntil);
        var evidence = Assert.Single(resolution.Evidence);
        Assert.Equal(4740, evidence.EventCode);
        Assert.Empty(_source.Queries);
    }

    [Fact]
    public async Task AnEmptyCaller_QueriesThePdcAndTheLockoutDc()
    {
        Add4740(caller: null);
        for (var i = 0; i < 3; i++)
        {
            AddFailure(Pdc, id => SecurityEventXml.KerberosPreAuthFailed(id, LockedAt.AddMinutes(-5 - i), Pdc, "lwisniewski", "10.20.30.41"));
        }

        AddFailure(Pdc, id => SecurityEventXml.KerberosPreAuthFailed(id, LockedAt.AddMinutes(-2), Pdc, "lwisniewski", "10.20.30.99", status: "0x12"));
        AddFailure(Dc1, id => SecurityEventXml.CredentialValidation(id, LockedAt.AddMinutes(-1), Dc1, "lwisniewski", "PC-GDANSK-01"));
        AddFailure(Dc1, id => SecurityEventXml.CredentialValidation(id, LockedAt.AddMinutes(-4), Dc1, "lwisniewski", "PC-GDANSK-01"));
        AddFailure(Dc1, id => SecurityEventXml.LogonFailed(id, LockedAt.AddMinutes(-6), Dc1, "lwisniewski", "10.20.30.77", "SRV-POCZTA"));
        AddFailure(Dc1, id => SecurityEventXml.LogonFailed(id, LockedAt.AddMinutes(-31), Dc1, "lwisniewski", "10.20.30.78", null));
        AddFailure(Dc1, id => SecurityEventXml.LogonFailed(id, LockedAt.AddMinutes(-3), Dc1, "zzak", "10.20.30.79", null));

        var resolution = await _resolver.ResolveAsync(Lockout(), Settings, Ct);

        Assert.Equal(
        [
            new LockoutSource("10.20.30.41", null, 3, 4771),
            new LockoutSource(null, "PC-GDANSK-01", 2, 4776),
            new LockoutSource("10.20.30.77", "SRV-POCZTA", 1, 4625),
        ], resolution.Lockout.Sources);
        Assert.Equal([Pdc, Dc1], _source.Queries.Select(q => q.Host));
        Assert.All(_source.Queries, q => Assert.Equal([("TargetUserName", "lwisniewski")], q.Query.DataEquals));
    }

    [Fact]
    public async Task ADcAsCaller_AlsoTriggersTheLookup()
    {
        Add4740("DC01");
        AddFailure(Pdc, id => SecurityEventXml.KerberosPreAuthFailed(id, LockedAt.AddMinutes(-1), Pdc, "lwisniewski", "10.20.30.41"));

        var resolution = await _resolver.ResolveAsync(Lockout(), Settings, Ct);

        Assert.Equal(2, resolution.Lockout.Sources.Count);
        Assert.Contains(resolution.Lockout.Sources, s => s.Workstation == "DC01");
    }

    [Fact]
    public async Task LookupsOff_KeepOnlyTheCaller()
    {
        Add4740(caller: null);

        var resolution = await _resolver.ResolveAsync(Lockout(), Settings with { LookupLockoutSources = false }, Ct);

        Assert.Empty(resolution.Lockout.Sources);
        Assert.Empty(_source.Queries);
    }

    [Fact]
    public async Task WithoutA4740_OnlyThePdcIsQueried()
    {
        var resolution = await _resolver.ResolveAsync(Lockout(), Settings, Ct);

        Assert.Empty(resolution.Evidence);
        Assert.Null(resolution.Lockout.CallerComputer);
        Assert.Equal([Pdc], _source.Queries.Select(q => q.Host));
    }

    [Theory]
    [InlineData("józef.żak")]
    [InlineData("o'neill")]
    [InlineData("Łukasz.Wiśniewski")]
    public async Task AccountNamesWithDiacriticsOrApostrophes_AreLookedUp(string sam)
    {
        // attribution-rules-16: Polish sAMAccountNames must not silently skip the bad-password lookup.
        _store.Add(Parse(SecurityEventXml.Create(4740, NextId(), LockedAt, Dc1,
            [("TargetUserName", sam), ("TargetDomainName", null), ("TargetSid", UserSid), .. LocalSystem])));
        AddFailure(Pdc, id => SecurityEventXml.KerberosPreAuthFailed(id, LockedAt.AddMinutes(-1), Pdc, sam, "10.20.30.41"));
        var lockout = Lockout() with { Object = Lockout().Object with { SamAccountName = sam } };

        var resolution = await _resolver.ResolveAsync(lockout, Settings, Ct);

        Assert.Equal([new LockoutSource("10.20.30.41", null, 1, 4771)], resolution.Lockout.Sources);
        Assert.All(_source.Queries, q => Assert.Equal([("TargetUserName", sam)], q.Query.DataEquals));
        Assert.True(resolution.Complete);
    }

    [Fact]
    public async Task WithoutA4740_TheResolutionIsIncomplete()
    {
        var resolution = await _resolver.ResolveAsync(Lockout(), Settings, Ct);

        Assert.False(resolution.Complete);
    }

    [Fact]
    public async Task AnUnfinishedLookup_LeavesTheResolutionIncomplete()
    {
        Add4740(caller: null);
        _source.Fail(Pdc);

        var resolution = await _resolver.ResolveAsync(Lockout(), Settings, Ct);

        Assert.False(resolution.Complete);
    }

    [Fact]
    public async Task AnUnreachableDc_IsSkipped()
    {
        Add4740(caller: null);
        _source.Fail(Pdc);
        AddFailure(Dc1, id => SecurityEventXml.KerberosPreAuthFailed(id, LockedAt.AddMinutes(-1), Dc1, "lwisniewski", "10.20.30.41"));

        var resolution = await _resolver.ResolveAsync(Lockout(), Settings, Ct);

        Assert.Equal([new LockoutSource("10.20.30.41", null, 1, 4771)], resolution.Lockout.Sources);
    }
}
