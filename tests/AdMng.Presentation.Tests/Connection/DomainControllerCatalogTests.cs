using AdMng.Core.Directory;
using AdMng.Presentation.Connection;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Connection;

/// <summary>
/// The domain controllers offered by "Change domain controller": read from the configuration partition of the current
/// session (site, GC, RODC, PDC emulator) and merged with what the DC locator knows (client site).
/// </summary>
public class DomainControllerCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeDirectorySession Directory()
    {
        var builder = new FakeDirectoryBuilder();
        builder.AddSampleCompany();
        var session = builder.Session;
        DomainControllerSeed.Add(session, "dc01.contoso.test", "Warszawa", globalCatalog: true, pdc: true);
        DomainControllerSeed.Add(session, "dc02.contoso.test", "Kraków", globalCatalog: false);
        DomainControllerSeed.Add(session, "rodc-gdańsk.contoso.test", "Gdańsk", readOnly: true);
        DomainControllerSeed.Add(session, "dc01.oddział.contoso.test", "Kraków", domainNc: "DC=oddział,DC=contoso,DC=test");
        return session;
    }

    private static DomainControllerEntry Entry(string host, string? site, bool inSite = false, bool gc = false, bool rodc = false, bool pdc = false, bool current = false) =>
        new(host, site, inSite, gc, rodc, pdc, current);

    [Fact]
    public async Task ReadAsync_ListsTheDomainsControllers_WithSiteRolesAndPdc()
    {
        var session = Directory();

        var controllers = await DomainControllerCatalog.ReadAsync(session, Ct);

        Assert.Equal(
            [
                new DirectoryDomainController("dc01.contoso.test", "Warszawa", IsGlobalCatalog: true, IsReadOnly: false, IsPdcEmulator: true),
                new DirectoryDomainController("dc02.contoso.test", "Kraków", IsGlobalCatalog: false, IsReadOnly: false, IsPdcEmulator: false),
                new DirectoryDomainController("rodc-gdańsk.contoso.test", "Gdańsk", IsGlobalCatalog: true, IsReadOnly: true, IsPdcEmulator: false),
            ],
            controllers.OrderBy(c => c.HostName, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_OnlyReads()
    {
        var session = Directory();

        await DomainControllerCatalog.ReadAsync(session, Ct);

        Assert.Empty(session.Writes);
        Assert.All(session.Operations, o => Assert.False(o.IsWrite));
    }

    [Fact]
    public async Task ReadAsync_WithoutSites_ReturnsNothing()
    {
        var builder = new FakeDirectoryBuilder();
        builder.AddSampleCompany();

        Assert.Empty(await DomainControllerCatalog.ReadAsync(builder.Session, Ct));
    }

    [Fact]
    public void Merge_AddsTheClientSiteFromTheLocator_AndDnsOnlyControllers()
    {
        var info = FakeDirectorySession.CreateInfo();
        DirectoryDomainController[] directory =
        [
            new("dc01.contoso.test", "Warszawa", true, false, true),
            new("dc02.contoso.test", "Kraków", false, false, false),
            new("dc04.contoso.test", "Kraków", true, false, false),
            new("rodc-gdańsk.contoso.test", "Gdańsk", true, true, false),
        ];
        DomainControllerInfo[] located =
        [
            new("DC04.contoso.test", "Kraków", true, false, IsInClientSite: true),
            new("dc02.contoso.test", "Kraków", false, false, IsInClientSite: true),
            new("dc09.contoso.test", null, false, false, IsInClientSite: false),
            new("contoso.test", null, false, false, false),
        ];

        var list = DomainControllerCatalog.Merge(directory, located, info);

        Assert.Equal("Kraków", list.ClientSite);
        Assert.Equal(
            [
                Entry("dc04.contoso.test", "Kraków", inSite: true, gc: true),
                Entry("dc02.contoso.test", "Kraków", inSite: true),
                Entry("dc09.contoso.test", null),
                Entry("dc01.contoso.test", "Warszawa", gc: true, pdc: true, current: true),
                Entry("rodc-gdańsk.contoso.test", "Gdańsk", gc: true, rodc: true),
            ],
            list.Entries);
    }

    [Fact]
    public void Merge_WithoutDirectoryFacts_UsesTheLocatorList_AndAlwaysListsTheCurrentDc()
    {
        var info = FakeDirectorySession.CreateInfo(server: "DC07.contoso.test");
        DomainControllerInfo[] located =
        [
            new("dc01.contoso.test", "Warszawa", true, false, true),
            new("dc02.contoso.test", "Kraków", true, true, false),
        ];

        var list = DomainControllerCatalog.Merge([], located, info);

        Assert.Equal("Warszawa", list.ClientSite);
        Assert.Equal(
            [
                Entry("dc01.contoso.test", "Warszawa", inSite: true, gc: true),
                Entry("DC07.contoso.test", null, current: true),
                Entry("dc02.contoso.test", "Kraków", gc: true, rodc: true),
            ],
            list.Entries);
        Assert.Same(list.Entries[1], list.Find("dc07.CONTOSO.test."));
        Assert.Same(list.Entries[1], list.Current);
    }

    [Fact]
    public void Merge_WithoutLocator_KnowsNoClientSite()
    {
        var info = FakeDirectorySession.CreateInfo();
        DirectoryDomainController[] directory = [new("dc01.contoso.test", "Warszawa", true, false, true), new("dc02.contoso.test", "Warszawa", true, false, false)];

        var list = DomainControllerCatalog.Merge(directory, [new("contoso.test", null, false, false, false)], info);

        Assert.Null(list.ClientSite);
        Assert.All(list.Entries, e => Assert.False(e.IsInClientSite));
        Assert.Null(list.AnyWritableInClientSite);
    }

    [Fact]
    public void AnyWritableInClientSite_IsTheCurrentDcWhenItQualifies()
    {
        var info = FakeDirectorySession.CreateInfo();
        DomainControllerInfo[] located =
        [
            new("dc02.contoso.test", "Warszawa", true, false, true),
            new("dc01.contoso.test", "Warszawa", true, false, true),
        ];

        var list = DomainControllerCatalog.Merge([], located, info);

        Assert.Equal("dc01.contoso.test", list.AnyWritableInClientSite?.HostName);
        Assert.True(list.AnyWritableInClientSite!.IsCurrent);
    }

    [Fact]
    public void AnyWritableInClientSite_SkipsReadOnlyControllers_AndFollowsTheLocatorOrder()
    {
        var info = FakeDirectorySession.CreateInfo(server: "dc09.contoso.test");
        DomainControllerInfo[] located =
        [
            new("rodc01.contoso.test", "Łódź", true, true, true),
            new("dc05.contoso.test", "Łódź", true, false, true),
            new("dc03.contoso.test", "Łódź", true, false, true),
        ];

        var list = DomainControllerCatalog.Merge([], located, info);

        Assert.Equal("dc05.contoso.test", list.AnyWritableInClientSite?.HostName);
    }

    [Fact]
    public void AnyWritableInClientSite_IsNullWhenTheSiteHasOnlyAReadOnlyDc()
    {
        var info = FakeDirectorySession.CreateInfo(server: "dc09.contoso.test");

        var list = DomainControllerCatalog.Merge([], [new("rodc01.contoso.test", "Łódź", true, true, true)], info);

        Assert.Equal("Łódź", list.ClientSite);
        Assert.Null(list.AnyWritableInClientSite);
    }

    [Theory]
    [InlineData("dc02.contoso.test", "dc02.contoso.test")]
    [InlineData("  DC02.contoso.test.  ", "DC02.contoso.test")]
    [InlineData("dc02", "dc02")]
    [InlineData("10.0.0.12", "10.0.0.12")]
    [InlineData("fe80::1", "fe80::1")]
    [InlineData("dc-łódź.contoso.test", "dc-łódź.contoso.test")]
    public void HostNames_AreNormalized(string text, string expected)
    {
        Assert.True(DomainControllerCatalog.TryNormalizeHostName(text, out var host));
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dc02.contoso.test:636")]
    [InlineData("ldap://dc02.contoso.test")]
    [InlineData("dc 02")]
    [InlineData(@"CONTOSO\dc02")]
    [InlineData("dc02/contoso")]
    [InlineData("(dc02)")]
    public void InvalidHostNames_AreRejected(string text)
    {
        Assert.False(DomainControllerCatalog.TryNormalizeHostName(text, out _));
    }

    [Theory]
    [InlineData("dc01.contoso.test", true)]
    [InlineData("DC01.CONTOSO.TEST.", true)]
    [InlineData("dc01", true)]
    [InlineData("dc01.oddział.contoso.test", false)]
    [InlineData("dc02.contoso.test", false)]
    public void IsCurrent_MatchesTheFullNameOrTheFirstLabel(string host, bool expected)
    {
        Assert.Equal(expected, DomainControllerCatalog.IsSameHost(host, "dc01.contoso.test"));
    }
}
