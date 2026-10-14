using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Tests.Attribution.Fakes;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class OriginatingDcResolverTests
{
    private static readonly AttributionSettings Settings = new();
    private static readonly string[] Sources = [Dc1, Dc2];
    private static readonly DateTimeOffset Originating = Now.AddMinutes(-4);

    [Fact]
    public void OriginatingTimeAndKnownDc_SearchesThatDcOnly()
    {
        var e = Create(WatchEventKind.AccountDisabled) with { OriginatingTime = Originating, OriginatingDc = "DC02.Contoso.Test." };

        var plan = OriginatingDcResolver.Plan(e, Sources, Settings);

        Assert.Equal([Dc2], plan.DomainControllers);
        Assert.Equal(Originating.AddSeconds(-3), plan.From);
        Assert.Equal(Originating.AddSeconds(3), plan.To);
        Assert.Equal(Originating, plan.ReferenceTime);
        Assert.True(plan.HasOriginatingTime);
        Assert.Equal(Dc2, plan.OriginatingDc);
    }

    [Fact]
    public void OriginatingDcOutsideTheSources_WidensToEverySource()
    {
        var e = Create(WatchEventKind.AccountDisabled) with { OriginatingTime = Originating, OriginatingDc = "rodc01.contoso.test" };

        var plan = OriginatingDcResolver.Plan(e, Sources, Settings);

        Assert.Equal(Sources, plan.DomainControllers);
        Assert.True(plan.HasOriginatingTime);
    }

    [Fact]
    public void NoMetadata_UsesTheLocalChangeTimeAndTheFallbackWindow()
    {
        var local = Now.AddMinutes(-1);
        var e = Create(WatchEventKind.GroupMemberAdded) with { LocalChangeTime = local };

        var plan = OriginatingDcResolver.Plan(e, Sources, Settings);

        Assert.Equal(Sources, plan.DomainControllers);
        Assert.Equal(local.AddMinutes(-15), plan.From);
        Assert.Equal(local.AddMinutes(2), plan.To);
        Assert.False(plan.HasOriginatingTime);
    }

    [Fact]
    public void NoTimesAtAll_UsesTheDetectionTime()
    {
        var plan = OriginatingDcResolver.Plan(Create(WatchEventKind.UserCreated), Sources, Settings);

        Assert.Equal(Now, plan.ReferenceTime);
        Assert.Equal(Now.AddMinutes(-15), plan.From);
    }

    [Fact]
    public async Task InvocationIds_ResolveThroughTheDcCatalog()
    {
        var invocation = Guid.NewGuid();
        var catalog = new FakeDomainControllers(Dc1, Dc2);
        catalog.InvocationIds[invocation] = Dc2;
        var resolver = new OriginatingDcResolver(catalog);

        Assert.Equal(Dc2, await resolver.ResolveHostAsync(invocation, TestContext.Current.CancellationToken));
        Assert.Null(await resolver.ResolveHostAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
}
