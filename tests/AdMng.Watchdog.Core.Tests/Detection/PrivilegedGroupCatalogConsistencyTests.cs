using AdMng.Core.PrivilegedAccess;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>
/// The Watchdog's configurable default tiers (Contracts, no references) stay consistent with the privileged group
/// catalogue of AdMng.Core that the account review (U2, U4) uses: every Critical root is a Tier 0 group of the catalogue,
/// every High root at least Elevated, and the forest-root RIDs come from the catalogue.
/// </summary>
public class PrivilegedGroupCatalogConsistencyTests
{
    private const string Domain = "S-1-5-21-10-20-30";

    [Fact]
    public void CriticalDefaults_AreTier0GroupsOfTheCatalogue()
    {
        var settings = new PrivilegedGroupSettings();

        foreach (var template in settings.CriticalSids)
        {
            var group = PrivilegedGroupCatalog.Match(Expand(template), null, Domain, Domain);
            Assert.True(group is not null, $"{template} is not in the catalogue");
            Assert.Equal(PrivilegeLevel.Tier0, group.DefaultLevel);
        }
    }

    [Fact]
    public void HighDefaults_AreAtLeastElevatedGroupsOfTheCatalogue()
    {
        var settings = new PrivilegedGroupSettings();

        foreach (var template in settings.HighSids)
        {
            var group = PrivilegedGroupCatalog.Match(Expand(template), null, Domain, Domain);
            Assert.True(group is not null, $"{template} is not in the catalogue");
            Assert.True(group.DefaultLevel >= PrivilegeLevel.Elevated, $"{template}: {group.DefaultLevel}");
        }

        Assert.All(settings.HighSamAccountNames, name => Assert.NotNull(PrivilegedGroupCatalog.Match(null, name, Domain, Domain)));
    }

    [Fact]
    public void UnknownRootDomain_TheForestRootTier0Rids_MatchInAnyDomain()
    {
        var facts = new DirectoryFacts
        {
            DomainDn = "DC=child,DC=contoso,DC=test",
            DomainDnsName = "child.contoso.test",
            ConfigurationDn = "CN=Configuration,DC=contoso,DC=test",
            SchemaDn = "CN=Schema,CN=Configuration,DC=contoso,DC=test",
            DomainSid = Domain,
            RootDomainSid = null,
        };
        var set = new PrivilegedGroupSet(new PrivilegedGroupSettings(), facts);

        foreach (var rid in PrivilegedGroupCatalog.ForestRootTier0Rids)
        {
            Assert.Equal(PrivilegeTier.Critical, set.TierOf($"S-1-5-21-77-88-99-{rid}", null, null));
        }

        Assert.Null(set.TierOf("S-1-5-21-77-88-99-498", null, null));
    }

    private static string Expand(string template) =>
        template.Replace("{domainSid}", Domain, StringComparison.Ordinal).Replace("{rootDomainSid}", Domain, StringComparison.Ordinal);
}
