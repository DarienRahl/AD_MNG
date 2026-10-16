using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.IntegrationTests.Detection;

/// <summary>
/// Facts the engine reads from the real DC with the read-only account (spec 10.2, 10.10, 10.11, 22.4): the binary
/// replPropertyMetaData (msDS-ReplAttributeMetaData is empty on Samba) names the lab DC as the originating DC, the DC
/// discovery through msDS-HasDomainNCs finds the lab DC (writable, PDC emulator), wellKnownObjects is parsed.
/// </summary>
public sealed class DirectoryFactsTests(ReadOnlyEngineFixture engine, LabFixture lab) : IClassFixture<ReadOnlyEngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EngineHost Host => engine.Host;

    [Fact]
    public async Task ReplicationMetadata_NamesTheLabDcAndTheTimeOfTheChange()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Metadane", Ct);
        var user = await lab.Writer.CreateUserAsync(ou, "Wiesława", "Pawlak", TestNames.Password(), cancellationToken: Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        var changedAt = DateTimeOffset.UtcNow;
        await lab.Writer.ReplaceAsync(user.Dn, Attr.Department, "Logistyka – Łódź", Ct);
        await Host.RunDetectionAsync(Ct);

        var changed = EventAssert.Single(await Host.EventsAsync(ou, mark, Ct), WatchEventKind.AttributesChanged, user.Dn);
        Assert.Equal(await LabDcAsync(), changed.OriginatingDc, StringComparer.OrdinalIgnoreCase);
        var originating = Assert.IsType<DateTimeOffset>(changed.OriginatingTime);
        Assert.InRange(originating, changedAt.AddMinutes(-2), changedAt.AddMinutes(2));
    }

    [Fact]
    public async Task DomainControllerDiscovery_FindsTheLabDc()
    {
        WatchdogLab.RequireLab();
        var dc = await LabDcAsync();
        var catalog = Host.Services.GetRequiredService<IDomainControllerCatalog>();

        Assert.True(catalog.IsAuthoritative);
        Assert.Contains(dc, await catalog.GetAttributionSourcesAsync(Ct), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(dc, await catalog.GetKnownDomainControllersAsync(Ct), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(dc, await catalog.GetPdcHostAsync(Ct), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WellKnownObjects_AreParsed()
    {
        WatchdogLab.RequireLab();
        var session = await Host.Sessions.GetSessionAsync(Ct);

        var state = await Host.Services.GetRequiredService<DcStateReader>().ReadAsync(session, Ct);

        var domain = DistinguishedName.Parse(WatchdogLab.DomainDn);
        Assert.Equal(domain.Child("CN", "Users"), state.Containers.Users);
        Assert.Equal(domain.Child("CN", "Computers"), state.Containers.Computers);
        Assert.False(state.Containers.ComputersRedirected);
        Assert.True(state.IsPdc);
        Assert.Equal(await LabDcAsync(), state.DnsHostName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>DNS host name of the lab DC (RootDSE dnsHostName).</summary>
    private async Task<string> LabDcAsync()
    {
        var rootDse = await lab.Admin.GetAsync(string.Empty, [Attr.DnsHostNameRootDse], Ct);
        return Assert.IsType<string>(rootDse?.GetString(Attr.DnsHostNameRootDse));
    }
}
