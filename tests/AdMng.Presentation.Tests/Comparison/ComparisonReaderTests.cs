using AdMng.Core.ActiveDirectory;
using AdMng.Core.Comparison;
using AdMng.Core.Directory;
using AdMng.Presentation.Comparison;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Comparison;

public sealed class ComparisonReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadAsync_CompletesRangedValues_AndKeepsTheSidesFacts()
    {
        var domains = await ComparisonDomains.CreateAsync(contosoExtra: b =>
        {
            var members = Enumerable.Range(0, 7).Select(i => b.AddUser("Użytkownik", $"Numer{i}")).ToArray();
            b.AddGroup("GG_Duża", members: members);
        });
        var contoso = domains.Contoso.Session;
        contoso.MaxValueRange = 3;
        var groupDn = contoso.DistinguishedNames.Single(d => d.StartsWith("CN=GG_Duża", StringComparison.Ordinal));

        var snapshot = await ComparisonReader.ReadAsync(ReadOnlyDirectoryReader.Of(contoso), groupDn, Ct);

        Assert.NotNull(snapshot);
        Assert.Equal(7, snapshot.Attributes[Attr.Member].Count);
        Assert.Empty(snapshot.TruncatedAttributes);
        Assert.Equal("contoso.test", snapshot.DomainDnsName);
        Assert.Equal("DC=contoso,DC=test", snapshot.DefaultNamingContext);
        Assert.NotNull(snapshot.ObjectGuid);
        Assert.NotNull(snapshot.ObjectSid);
        Assert.Contains(contoso.Operations, o => o.Kind == FakeOperationKind.GetAllValues);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNullForAMissingObject()
    {
        var domains = await ComparisonDomains.CreateAsync();

        var snapshot = await ComparisonReader.ReadAsync(ReadOnlyDirectoryReader.Of(domains.Contoso.Session), "CN=Nikt,DC=contoso,DC=test", Ct);

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task Membership_DirectPrimaryAndNested_PerSide()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var contoso = ReadOnlyDirectoryReader.Of(domains.Contoso.Session);
        var lukasz = (await ComparisonReader.ReadAsync(contoso, domains.Contoso.Company.LukaszWisniewski, Ct))!;

        var direct = await ComparisonReader.ReadDirectMembershipAsync(contoso, lukasz, domains.Contoso.Session.DomainSid, Ct);
        var nested = await ComparisonReader.ReadNestedMembershipAsync(contoso, lukasz, direct, Ct);

        Assert.Contains(direct.Entries, e => e is { Name: "GG_IT", Kind: MembershipKind.Direct, Key: "sam:GG_IT" });
        Assert.Contains(direct.Entries, e => e is { Name: "Domain Users", Kind: MembershipKind.Primary, Key: "rid:513" });
        Assert.NotNull(direct.PrimaryGroupDn);

        // DL_Wszyscy contains GG_IT: a nested membership (LDAP_MATCHING_RULE_IN_CHAIN), not listed twice.
        var all = Assert.Single(nested.Entries);
        Assert.Equal(("DL_Wszyscy", MembershipKind.Nested), (all.Name, all.Kind));
        Assert.Contains(domains.Contoso.Session.Operations, o => o.Search?.Filter.Contains(Core.Ldap.LdapFilter.MatchingRuleInChain, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task WellKnownGroups_MatchByRid_AcrossLanguages()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var contoso = ReadOnlyDirectoryReader.Of(domains.Contoso.Session);
        var fabrikam = ReadOnlyDirectoryReader.Of(domains.FabrikamDomain.Session);
        var a = (await ComparisonReader.ReadAsync(contoso, domains.Contoso.Company.LukaszWisniewski, Ct))!;
        var b = (await ComparisonReader.ReadAsync(fabrikam, domains.FabrikamDomain.Company.LukaszWisniewski, Ct))!;
        var directA = await ComparisonReader.ReadDirectMembershipAsync(contoso, a, null, Ct);
        var directB = await ComparisonReader.ReadDirectMembershipAsync(fabrikam, b, null, Ct);

        var rows = MembershipComparer.Compare(directA.Entries, directB.Entries).ToDictionary(r => r.Key);

        var domainUsers = rows["rid:513"];
        Assert.Equal(MembershipComparisonState.InBoth, domainUsers.State);
        Assert.Equal("Domain Users", domainUsers.A!.Name);
        Assert.Equal("Użytkownicy domeny", domainUsers.B!.Name);
        Assert.Equal(MembershipComparisonState.OnlyA, rows["sam:GG_IT"].State);
        Assert.Equal(MembershipComparisonState.OnlyB, rows["sam:GG_SPRZEDAZ"].State);
    }

    [Fact]
    public async Task Members_OfAGroup_SkipForeignPrincipals_AndReadNestedOnes()
    {
        var domains = await ComparisonDomains.CreateAsync(contosoExtra: b =>
        {
            var fsps = b.AddContainer("ForeignSecurityPrincipals");
            var fsp = b.Session.Seed($"CN=S-1-5-21-1-2-3-1105,{fsps}", new Dictionary<string, object?>
            {
                [Attr.ObjectClass] = new[] { "top", ObjectClasses.ForeignSecurityPrincipal },
                [Attr.ObjectSid] = "S-1-5-21-1-2-3-1105",
            });
            b.AddMembers(b.Session.DistinguishedNames.Single(d => d.StartsWith("CN=DL_Wszyscy", StringComparison.Ordinal)), fsp);
        });
        var contoso = ReadOnlyDirectoryReader.Of(domains.Contoso.Session);
        var all = (await ComparisonReader.ReadAsync(contoso, domains.Contoso.Company.GroupAll, Ct))!;

        var members = await ComparisonReader.ReadMembersAsync(contoso, all, Ct);
        var nested = await ComparisonReader.ReadNestedMembersAsync(contoso, all, members, Ct);

        Assert.Equal(["GG_IT", "GG_Ksiegowosc", "GG_Sprzedaz"], members.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, members.Skipped);
        Assert.Contains(nested.Entries, e => e is { Name: "Łukasz Wiśniewski", Kind: MembershipKind.Nested, Key: "sam:LUKASZ.WISNIEWSKI" });
        Assert.DoesNotContain(nested.Entries, e => e.Name == "GG_IT");
    }
}
