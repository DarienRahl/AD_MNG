using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Comparison;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Comparison;

public sealed class ObjectMatcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Automatic_FindsAUserBySamAccountNameFirst()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var source = await SourceAsync(domains.Contoso.Session, domains.Contoso.Company.LukaszWisniewski);

        var result = await ObjectMatcher.MatchAsync(Reader(domains.FabrikamDomain.Session), source, ObjectMatchRule.Automatic, null, Ct);

        Assert.Equal(MatchStatus.Found, result.Status);
        Assert.Equal(ObjectMatchRule.SamAccountName, result.Rule);
        Assert.Equal(domains.FabrikamDomain.Company.LukaszWisniewski, result.Match?.DistinguishedName);
        Assert.Equal([ObjectMatchRule.SamAccountName], result.RulesTried);
    }

    [Fact]
    public async Task Automatic_SkipsRulesWithoutASourceValue_AndFallsThroughToTheNextRule()
    {
        // In fabrikam Łukasz has another sAMAccountName and UPN, but the same mail.
        var domains = await ComparisonDomains.CreateAsync(fabrikamExtra: b =>
        {
            b.Session.SetValues(b.Session.DistinguishedNames.Single(d => d.StartsWith("CN=Łukasz", StringComparison.Ordinal)), Attr.SamAccountName, "lwisniewski");
            b.Session.SetValues(b.Session.DistinguishedNames.Single(d => d.StartsWith("CN=Łukasz", StringComparison.Ordinal)), Attr.UserPrincipalName, "lw@fabrikam.test");
            b.Session.SetValues(b.Session.DistinguishedNames.Single(d => d.StartsWith("CN=Łukasz", StringComparison.Ordinal)), Attr.Mail, "lukasz.wisniewski@contoso.test");
        });
        var contoso = domains.Contoso.Session;
        contoso.SetValues(domains.Contoso.Company.LukaszWisniewski, Attr.UserPrincipalName);
        var source = await SourceAsync(contoso, domains.Contoso.Company.LukaszWisniewski);

        var result = await ObjectMatcher.MatchAsync(Reader(domains.FabrikamDomain.Session), source, ObjectMatchRule.Automatic, null, Ct);

        Assert.Equal(MatchStatus.Found, result.Status);
        Assert.Equal(ObjectMatchRule.Mail, result.Rule);
        Assert.Equal([ObjectMatchRule.SamAccountName, ObjectMatchRule.Mail], result.RulesTried);
    }

    [Fact]
    public async Task UpnPrefix_MatchesThePartBeforeTheAt()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var source = await SourceAsync(domains.Contoso.Session, domains.Contoso.Company.ZanetaSzymanska);

        var result = await ObjectMatcher.MatchAsync(Reader(domains.FabrikamDomain.Session), source, ObjectMatchRule.UserPrincipalNamePrefix, null, Ct);

        Assert.Equal(MatchStatus.Found, result.Status);
        Assert.Equal(domains.FabrikamDomain.Company.ZanetaSzymanska, result.Match?.DistinguishedName);
        Assert.Contains(domains.FabrikamDomain.Session.Operations, o => o.Search?.Filter.Contains("(userPrincipalName=zaneta.szymanska@*)", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task GroupsComputersAndContacts_HaveTheirOwnRules()
    {
        var domains = await ComparisonDomains.CreateAsync(
            contosoExtra: b => b.AddContact("Żaneta", "Kontakt", attributes: new Dictionary<string, object?> { [Attr.Mail] = "zaneta@partner.test" }),
            fabrikamExtra: b => b.AddContact("Żaneta", "Kontakt", attributes: new Dictionary<string, object?> { [Attr.Mail] = "zaneta@partner.test" }));
        var target = Reader(domains.FabrikamDomain.Session);
        var contoso = domains.Contoso.Session;
        var group = await SourceAsync(contoso, domains.Contoso.Company.GroupSales);
        var computer = await SourceAsync(contoso, domains.Contoso.Company.Laptop);
        var contact = await SourceAsync(contoso, contoso.DistinguishedNames.Single(d => d.StartsWith("CN=Żaneta Kontakt", StringComparison.Ordinal)));

        var groupMatch = await ObjectMatcher.MatchAsync(target, group, ObjectMatchRule.Automatic, null, Ct);
        var computerMatch = await ObjectMatcher.MatchAsync(target, computer, ObjectMatchRule.Name, null, Ct);
        var contactMatch = await ObjectMatcher.MatchAsync(target, contact, ObjectMatchRule.Automatic, null, Ct);

        Assert.Equal((MatchStatus.Found, ObjectMatchRule.SamAccountName), (groupMatch.Status, groupMatch.Rule));
        Assert.Equal(domains.FabrikamDomain.Company.GroupSales, groupMatch.Match?.DistinguishedName);
        Assert.Equal(domains.FabrikamDomain.Company.Laptop, computerMatch.Match?.DistinguishedName);
        Assert.Equal((MatchStatus.Found, ObjectMatchRule.Mail), (contactMatch.Status, contactMatch.Rule));
        Assert.Equal([ObjectMatchRule.Mail, ObjectMatchRule.Name], ObjectMatcher.AutomaticRules(ObjectKind.Contact));
        Assert.DoesNotContain(ObjectMatchRule.UserPrincipalNamePrefix, ObjectMatcher.Choices(ObjectKind.Computer));
    }

    [Fact]
    public async Task NoMatch_SaysNotFound_WithTheRulesTried()
    {
        var domains = await ComparisonDomains.CreateAsync(contosoExtra: b => b.AddUser("Grzegorz", "Brzęczyszczykiewicz"));
        var contoso = domains.Contoso.Session;
        var source = await SourceAsync(contoso, contoso.DistinguishedNames.Single(d => d.StartsWith("CN=Grzegorz", StringComparison.Ordinal)));

        var result = await ObjectMatcher.MatchAsync(Reader(domains.FabrikamDomain.Session), source, ObjectMatchRule.Automatic, null, Ct);

        Assert.Equal(MatchStatus.NotFound, result.Status);
        Assert.Null(result.Match);
        Assert.Equal([ObjectMatchRule.SamAccountName, ObjectMatchRule.UserPrincipalNamePrefix, ObjectMatchRule.Name], result.RulesTried);
    }

    [Fact]
    public async Task SeveralResults_AreCandidates_AndMoreThanFifty_AskToNarrow()
    {
        var domains = await ComparisonDomains.CreateAsync(fabrikamExtra: b =>
        {
            var ou = b.AddOu("Kopie");
            for (var i = 0; i < 2; i++)
            {
                b.AddUser("Łukasz", "Wiśniewski", b.AddOu($"Oddział {i}", ou), new Dictionary<string, object?> { [Attr.SamAccountName] = $"lwisniewski{i}" });
            }

            for (var i = 0; i < 51; i++)
            {
                b.AddUser("Paweł", $"Lewandowski{i:D2}", ou, new Dictionary<string, object?> { [Attr.Mail] = "sprzedaz@fabrikam.test" });
            }
        });
        var contoso = domains.Contoso.Session;
        var lukasz = await SourceAsync(contoso, domains.Contoso.Company.LukaszWisniewski);
        contoso.SetValues(domains.Contoso.Company.PawelLewandowski, Attr.Mail, "sprzedaz@fabrikam.test");
        var pawel = await SourceAsync(contoso, domains.Contoso.Company.PawelLewandowski);
        var target = Reader(domains.FabrikamDomain.Session);

        var byName = await ObjectMatcher.MatchAsync(target, lukasz, ObjectMatchRule.Name, null, Ct);
        var byMail = await ObjectMatcher.MatchAsync(target, pawel, ObjectMatchRule.Mail, null, Ct);

        Assert.Equal(MatchStatus.Ambiguous, byName.Status);
        Assert.Equal(3, byName.Candidates.Count);
        Assert.Equal(MatchStatus.TooMany, byMail.Status);
        Assert.Empty(byMail.Candidates);
    }

    [Fact]
    public async Task Explicit_ReadsADnOfTheTargetDomain_AndRefusesOneOutsideIt()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var source = await SourceAsync(domains.Contoso.Session, domains.Contoso.Company.LukaszWisniewski);
        var target = Reader(domains.FabrikamDomain.Session);

        var inside = await ObjectMatcher.MatchAsync(target, source, ObjectMatchRule.Explicit, domains.FabrikamDomain.Company.BartlomiejKozlowski, Ct);
        var outside = await ObjectMatcher.MatchAsync(target, source, ObjectMatchRule.Explicit, domains.Contoso.Company.BartlomiejKozlowski, Ct);
        var missing = await ObjectMatcher.MatchAsync(target, source, ObjectMatchRule.Explicit, "CN=Nikt,OU=IT,OU=Pracownicy,OU=Firma,DC=fabrikam,DC=test", Ct);
        var empty = await ObjectMatcher.MatchAsync(target, source, ObjectMatchRule.Explicit, "  ", Ct);

        Assert.Equal(domains.FabrikamDomain.Company.BartlomiejKozlowski, inside.Match?.DistinguishedName);
        Assert.Equal(MatchStatus.OutsideNamingContext, outside.Status);
        Assert.Equal(MatchStatus.NotFound, missing.Status);
        Assert.Equal(MatchStatus.NoValue, empty.Status);
        Assert.DoesNotContain(domains.FabrikamDomain.Session.Operations, o => o.DistinguishedName.Contains("contoso", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Explicit_SearchesTextWithAnr()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var source = await SourceAsync(domains.Contoso.Session, domains.Contoso.Company.LukaszWisniewski);

        var result = await ObjectMatcher.MatchAsync(Reader(domains.FabrikamDomain.Session), source, ObjectMatchRule.Explicit, "Kozłowski", Ct);

        Assert.Equal(domains.FabrikamDomain.Company.BartlomiejKozlowski, result.Match?.DistinguishedName);
    }

    [Fact]
    public async Task EveryRule_EscapesTheSourceValues()
    {
        const string tricky = @"Łukasz (test)*\";
        var domains = await ComparisonDomains.CreateAsync(contosoExtra: b => b.AddUser("Łukasz", "(test)*\\", attributes: new Dictionary<string, object?>
        {
            [Attr.SamAccountName] = tricky,
            [Attr.UserPrincipalName] = tricky + "@contoso.test",
            [Attr.Mail] = tricky + "@contoso.test",
        }));
        var contoso = domains.Contoso.Session;
        var source = await SourceAsync(contoso, contoso.DistinguishedNames.Single(d => d.Contains("(test)", StringComparison.Ordinal)));
        var fabrikam = domains.FabrikamDomain.Session;

        foreach (var rule in ObjectMatcher.Choices(ObjectKind.User))
        {
            var result = await ObjectMatcher.MatchAsync(Reader(fabrikam), source, rule, rule == ObjectMatchRule.Explicit ? tricky : null, Ct);
            Assert.Equal(MatchStatus.NotFound, result.Status);
        }

        var filters = fabrikam.Operations.Where(o => o.Search is not null).Select(o => o.Search!.Filter).ToList();
        Assert.NotEmpty(filters);
        Assert.All(filters, f => Assert.DoesNotContain("(test)*\\", f, StringComparison.Ordinal));
        Assert.Contains(filters, f => f.Contains(@"\28test\29\2a\5c", StringComparison.OrdinalIgnoreCase));
        Assert.All(filters, f => Assert.Null(Core.Ldap.LdapFilter.Validate(f)));
    }

    private static IDirectoryReader Reader(FakeDirectorySession session) => ReadOnlyDirectoryReader.Of(session);

    private static async Task<DirectoryObject> SourceAsync(FakeDirectorySession session, string dn) =>
        (await session.GetAsync(dn, ObjectMatcher.Attributes, Ct))!;
}
