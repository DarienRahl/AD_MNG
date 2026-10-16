using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Tests.Attribution.Fakes;
using AdMng.Watchdog.Testing.Attribution;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;
using Microsoft.Extensions.Time.Testing;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public sealed class LogonSourceResolverTests : IDisposable
{
    private static readonly DateTimeOffset T = Now.AddMinutes(-1);
    private static readonly AttributionSettings Settings = new();

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeSecurityEventSource _source = new();
    private readonly FakeReverseDns _dns = new();
    private readonly DcLogHealthTracker _tracker;
    private readonly SourceQueryGate _gate;
    private readonly LogonSourceResolver _resolver;

    public LogonSourceResolverTests()
    {
        _tracker = new DcLogHealthTracker(_time);
        _gate = new SourceQueryGate(_tracker, _time);
        _resolver = new LogonSourceResolver(_source, _gate, _dns, _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _gate.Dispose();

    private void AddLogon(int logonType, string? ip, string? workstation, string package = "Kerberos", string logonId = AdminLogonId,
        DateTimeOffset? at = null, string host = Dc1) =>
        _source.Add(host, id => SecurityEventXml.Logon(id, at ?? T.AddHours(-2), host, logonId, logonType, ip, workstation, AdminSid, "adm.jkowalska", package));

    private Task<LogonSource?> ResolveAsync(SecurityEvent? evidence = null, AttributionConfidence actor = AttributionConfidence.High,
        AttributionSettings? settings = null) =>
        _resolver.ResolveAsync(evidence ?? Account(4725, T, Admin), actor, settings ?? Settings, Ct);

    [Fact]
    public async Task NetworkLogon_WithAddressAndReverseDns()
    {
        AddLogon(3, "::ffff:10.20.30.41", workstation: null);
        _dns.Names["10.20.30.41"] = "pc-kraków-07.contoso.test";

        var found = (await ResolveAsync())!;

        Assert.Equal("10.20.30.41", found.IpAddress);
        Assert.Equal("pc-kraków-07.contoso.test", found.Workstation);
        Assert.Equal("dns", found.SourceMethod);
        Assert.Equal(AttributionConfidence.Low, found.Confidence);
        Assert.Equal(3, found.LogonType);
        var query = Assert.Single(_source.Queries).Query;
        Assert.Equal([("TargetLogonId", AdminLogonId)], query.DataEquals);
        Assert.True(query.NewestFirst);
        Assert.Equal(T - Settings.LogonLookback, query.From);
        Assert.Equal(T, query.To);
    }

    [Fact]
    public async Task KerberosWithWorkstation_IsHigh_NtlmWorkstationIsLow()
    {
        AddLogon(3, "10.20.30.41", "PC-GDANSK-01");
        Assert.Equal(AttributionConfidence.High, (await ResolveAsync())!.Confidence);

        var other = Account(4725, T, SecurityEventXml.Subject(OtherAdminSid, "adm.zzak", "CONTOSO", "0x77aa1"));
        AddLogon(3, "10.20.30.42", "PC-GDANSK-02", "NTLM", logonId: "0x77aa1");
        var ntlm = (await ResolveAsync(other))!;
        Assert.Equal(AttributionConfidence.Low, ntlm.Confidence);
        Assert.Equal("4624", ntlm.SourceMethod);
        Assert.Equal(0, _dns.Calls);
    }

    [Fact]
    public async Task NoOrLoopbackAddress_IsLocal()
    {
        AddLogon(2, "127.0.0.1", "DC01");

        var found = (await ResolveAsync())!;

        Assert.Equal("local", found.SourceMethod);
        Assert.Null(found.IpAddress);
        Assert.Equal(2, found.LogonType);
    }

    [Fact]
    public async Task SourceConfidence_NeverExceedsTheActorConfidence()
    {
        AddLogon(10, "10.20.30.50", "JUMP-01");

        var found = (await ResolveAsync(actor: AttributionConfidence.Medium))!;

        Assert.Equal(AttributionConfidence.Medium, found.Confidence);
        Assert.Equal(10, found.LogonType);
    }

    [Fact]
    public async Task NothingFound_IsCachedNegatively()
    {
        var first = (await ResolveAsync())!;
        var second = (await ResolveAsync())!;

        Assert.Equal(AttributionReasons.SourceLogonNotFound, first.Reason);
        Assert.Equal(AttributionReasons.SourceLogonNotFound, second.Reason);
        Assert.Single(_source.Queries);
    }

    [Fact]
    public async Task TheCache_ExpiresAfter24Hours()
    {
        AddLogon(3, "10.20.30.41", "PC-01");
        await ResolveAsync();
        await ResolveAsync();
        _time.Advance(TimeSpan.FromHours(24));
        await ResolveAsync();

        Assert.Equal(2, _source.Queries.Count);
    }

    [Fact]
    public async Task SystemLogonsAndDisabledLookups_AreNotQueried()
    {
        Assert.Null(await ResolveAsync(Account(4738, T, LocalSystem)));
        Assert.Null(await ResolveAsync(settings: Settings with { LookupLogonSource = false }));
        Assert.Empty(_source.Queries);
    }

    [Fact]
    public async Task UnreadableLog_IsDcUnreachableAndNotCached()
    {
        _source.Fail(Dc1, FakeSecurityEventSource.AccessDenied);
        Assert.Equal(AttributionReasons.DcUnreachable, (await ResolveAsync())!.Reason);

        _source.Recover(Dc1);
        AddLogon(3, "10.20.30.41", "PC-01");
        Assert.Equal("10.20.30.41", (await ResolveAsync())!.IpAddress);
        Assert.True(_tracker.WasUnreachableSince(Dc1, Now.AddMinutes(-1)));
    }

    [Fact]
    public async Task ASlowDc_TimesOutOnTheTimeProvider()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.BeforeRead = async (_, _, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };

        var pending = ResolveAsync();
        await entered.Task.WaitAsync(Ct);
        Assert.False(pending.IsCompleted);
        _time.Advance(Settings.SourceQueryTimeout);
        var found = (await pending)!;

        Assert.Equal(AttributionReasons.DcUnreachable, found.Reason);
        var status = Assert.Single(_tracker.Snapshot([Dc1]));
        Assert.Equal(1, status.SourceLookupTimeouts24h);
    }

    [Fact]
    public async Task OneQueryPerDc_AtATime()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.BeforeRead = async (_, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var other = Account(4725, T, SecurityEventXml.Subject(OtherAdminSid, "adm.zzak", "CONTOSO", "0x77aa1"));

        var first = ResolveAsync();
        await entered.Task.WaitAsync(Ct);
        var second = ResolveAsync(other);
        Assert.Single(_source.Queries);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, _source.Queries.Count);
    }

    [Fact]
    public async Task ForwardedEvents_ReadsTheCollectorAndFiltersByComputer()
    {
        var settings = Settings with { Source = AttributionSourceKind.ForwardedEvents, Collector = "wec01.contoso.test" };
        _source.Add("wec01.contoso.test", id => SecurityEventXml.Logon(id, T.AddHours(-1), Dc2, AdminLogonId, 3, "10.9.9.9", "PC-INNY", AdminSid, "adm.jkowalska"), "ForwardedEvents");
        _source.Add("wec01.contoso.test", id => SecurityEventXml.Logon(id, T.AddHours(-2), Dc1, AdminLogonId, 3, "10.20.30.41", "PC-01", AdminSid, "adm.jkowalska"), "ForwardedEvents");

        var found = (await ResolveAsync(settings: settings))!;

        Assert.Equal("10.20.30.41", found.IpAddress);
        Assert.All(_source.Queries, q => Assert.Equal("ForwardedEvents", q.Query.LogName));
    }

    [Fact]
    public async Task ForwardedEventsWithout4624_IsNotCollected()
    {
        var settings = Settings with { Source = AttributionSourceKind.ForwardedEvents, Collector = "wec01.contoso.test" };

        var found = (await ResolveAsync(settings: settings))!;

        Assert.Equal(AttributionReasons.SourceNotCollected, found.Reason);
    }

    [Theory]
    [InlineData("::ffff:10.0.0.5", "10.0.0.5")]
    [InlineData("fe80::1", "fe80::1")]
    [InlineData(" 10.0.0.5 ", "10.0.0.5")]
    [InlineData("", null)]
    public void NormalizeAddress(string input, string? expected) => Assert.Equal(expected, LogonSourceResolver.NormalizeAddress(input));
}
