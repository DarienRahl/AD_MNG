using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Tests.Attribution.Fakes;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class AttributionSourceResolverTests
{
    private readonly FakeDomainControllers _dcs = new(Dc1, Dc2);
    private readonly FakeHealthRegistry _health = new();
    private readonly AttributionSourceResolver _resolver;

    public AttributionSourceResolverTests() => _resolver = new AttributionSourceResolver(_dcs, _health, new FakeTimeProvider(Now));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoConfiguredDcs_IngestsEveryWritableDc()
    {
        var sources = await _resolver.ResolveAsync(new AttributionSettings(), Ct);

        Assert.Equal([new IngestionSource(Dc1, "Security"), new IngestionSource(Dc2, "Security")], sources);
        Assert.Equal(HealthState.Ok, _health.Find("attribution.dcDirectory")!.State);
    }

    [Fact]
    public async Task ConfiguredDcs_MustBeWritableDcs()
    {
        var sources = await _resolver.ResolveAsync(new AttributionSettings { DomainControllers = ["DC02.contoso.test.", "srv-plików.contoso.test"] }, Ct);

        Assert.Equal([new IngestionSource(Dc2, "Security")], sources);
        var check = _health.Find("eventlog.srv-plików.contoso.test")!;
        Assert.Equal(HealthState.Error, check.State);
        Assert.Equal(AttributionSourceResolver.NotADomainController, check.Detail);
    }

    [Fact]
    public async Task ForwardedEvents_ReadsTheCollector()
    {
        var settings = new AttributionSettings { Source = AttributionSourceKind.ForwardedEvents, Collector = "WEC01.contoso.test" };

        var sources = await _resolver.ResolveAsync(settings, Ct);

        Assert.Equal([new IngestionSource("wec01.contoso.test", "ForwardedEvents")], sources);
        Assert.Equal([Dc1, Dc2], await _resolver.CorrelationHostsAsync(settings, sources, Ct));
    }

    [Fact]
    public async Task ForwardedEventsWithoutCollector_IsAnError()
    {
        var sources = await _resolver.ResolveAsync(new AttributionSettings { Source = AttributionSourceKind.ForwardedEvents }, Ct);

        Assert.Empty(sources);
        Assert.Equal(HealthState.Error, _health.Find("attribution.collector")!.State);
    }

    [Fact]
    public async Task AFallbackDcCatalog_IsAWarning()
    {
        _dcs.IsAuthoritative = false;

        await _resolver.ResolveAsync(new AttributionSettings(), Ct);

        Assert.Equal(HealthState.Warning, _health.Find("attribution.dcDirectory")!.State);
    }
}
