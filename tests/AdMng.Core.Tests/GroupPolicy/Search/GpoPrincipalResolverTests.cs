using AdMng.Core.GroupPolicy.Search;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.Search;

/// <summary>Name ↔ SID of GPO principals (21.3): without LDAP, then by LDAP on the session's DC, 50 names per search.</summary>
public sealed class GpoPrincipalResolverTests
{
    private const string DomainSid = FakeDirectorySession.DefaultDomainSid;

    // A domain whose NetBIOS name differs from the first label of its DNS name.
    private readonly FakeDirectoryBuilder _builder = new("ad.contoso.test", info: FakeDirectorySession.CreateInfo("ad.contoso.test") with { DomainNetBiosName = "CONTOSO" });
    private readonly SampleCompany _company;

    public GpoPrincipalResolverTests()
    {
        _company = _builder.AddSampleCompany();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("S-1-5-32-544", "S-1-5-32-544")]
    [InlineData("*S-1-5-32-544", "S-1-5-32-544")]
    [InlineData("*s-1-5-21-1-2-3-1105", "S-1-5-21-1-2-3-1105")]
    [InlineData("Everyone", "S-1-1-0")]
    [InlineData("Wszyscy", "S-1-1-0")]
    [InlineData("Użytkownicy uwierzytelnieni", "S-1-5-11")]
    [InlineData(@"BUILTIN\Administrators", "S-1-5-32-544")]
    [InlineData("Administratorzy (wbudowane)", "S-1-5-32-544")]
    [InlineData(@"CONTOSO\Domain Admins", DomainSid + "-512")]
    [InlineData("Administratorzy domeny", DomainSid + "-512")]
    [InlineData("Komputery domeny", DomainSid + "-515")]
    [InlineData("Administrator", DomainSid + "-500")]
    [InlineData(@"CONTOSO\jan.kowalski", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void KnownSid_WithoutLdap(string? principal, string? expected)
    {
        Assert.Equal(expected, GpoPrincipalResolver.KnownSid(principal, DomainSid));
    }

    [Fact]
    public async Task Resolve_DomainNames_Upn_BareNames_OtherDomainsUnresolved()
    {
        var jan = _builder.SidOf(_company.JanKowalski);
        var it = _builder.SidOf(_company.GroupIt);
        var lukasz = _builder.SidOf(_company.LukaszWisniewski);

        var results = await GpoPrincipalResolver.ResolveAsync(_builder.Session,
        [
            @"CONTOSO\jan.kowalski", @"AD\GG_IT", "lukasz.wisniewski@ad.contoso.test", "jan.kowalski", @"KONTOSO\jan.kowalski",
            "*S-1-5-32-544", "Wszyscy", @"CONTOSO\nieistniejący", @"contoso\JAN.KOWALSKI", "   ",
        ], DomainSid, Ct);

        Assert.Equal(
        [
            new GpoPrincipal(@"CONTOSO\jan.kowalski", jan, GpoPrincipalKind.Account),
            new GpoPrincipal(@"AD\GG_IT", it, GpoPrincipalKind.Account),
            new GpoPrincipal("lukasz.wisniewski@ad.contoso.test", lukasz, GpoPrincipalKind.Account),
            new GpoPrincipal("jan.kowalski", jan, GpoPrincipalKind.Account),
            new GpoPrincipal(@"KONTOSO\jan.kowalski", null, GpoPrincipalKind.Unresolved),
            new GpoPrincipal("*S-1-5-32-544", "S-1-5-32-544", GpoPrincipalKind.Sid),
            new GpoPrincipal("Wszyscy", "S-1-1-0", GpoPrincipalKind.WellKnown),
            new GpoPrincipal(@"CONTOSO\nieistniejący", null, GpoPrincipalKind.Unresolved),
            new GpoPrincipal("   ", null, GpoPrincipalKind.Unresolved),
        ], results); // the second spelling of CONTOSO\jan.kowalski is the same input
        Assert.Equal("Account", results[0].ToString());
    }

    [Fact]
    public async Task Resolve_FiftyNamesPerSearch_OnTheDomainNc_WithEscapedFilters()
    {
        var names = Enumerable.Range(0, 120).Select(i => $"użytkownik{i}").Append(@"CONTOSO\a*(b)\c").ToList();
        _builder.Session.ClearOperations();

        var results = await GpoPrincipalResolver.ResolveAsync(_builder.Session, names, DomainSid, Ct);

        var searches = _builder.Session.Operations.Where(o => o.Kind == FakeOperationKind.Search).ToList();
        Assert.Equal(3, searches.Count); // 121 sAMAccountName values: 50 + 50 + 21
        Assert.All(searches, s => Assert.Equal(_builder.Session.Info.DefaultNamingContext, s.DistinguishedName));
        Assert.Contains(searches, s => s.Search!.Filter.Contains(@"a\2a\28b\29\5cc", StringComparison.Ordinal));
        Assert.DoesNotContain(searches, s => s.Search!.Filter.Contains("a*(b)", StringComparison.Ordinal));
        Assert.All(results, r => Assert.Equal(GpoPrincipalKind.Unresolved, r.Kind));
        Assert.Equal(121, results.Count);
    }

    [Fact]
    public async Task Resolve_OnlyKnownNames_NoSearch()
    {
        _builder.Session.ClearOperations();

        var results = await GpoPrincipalResolver.ResolveAsync(_builder.Session, ["S-1-5-11", "BUILTIN\\Users"], DomainSid, Ct);

        Assert.Equal([GpoPrincipalKind.Sid, GpoPrincipalKind.WellKnown], results.Select(r => r.Kind));
        Assert.DoesNotContain(_builder.Session.Operations, o => o.Kind == FakeOperationKind.Search);
    }
}
