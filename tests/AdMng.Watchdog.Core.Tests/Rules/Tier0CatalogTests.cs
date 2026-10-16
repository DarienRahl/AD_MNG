using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class Tier0CatalogTests
{
    [Fact]
    public void Kinds_AreEverySecurityDomainAndAuditLogKind_PlusPrivilegeFlagsAndDelegation()
    {
        var expected = EventKindCatalog.All
            .Where(k => k.Category is EventCategory.Security or EventCategory.Domain or EventCategory.AuditLog)
            .Select(k => k.Kind)
            .ToHashSet();

        Assert.Superset(expected, Tier0Catalog.Kinds.ToHashSet());
        Assert.Contains(WatchEventKind.PrivilegeGranted, Tier0Catalog.Kinds);
        Assert.Contains(WatchEventKind.PrivilegeRevoked, Tier0Catalog.Kinds);
        Assert.Contains(WatchEventKind.AccountFlagsChanged, Tier0Catalog.Kinds);
        Assert.True(Tier0Catalog.IsTier0Kind(WatchEventKind.SecurityLogCleared));
        Assert.True(Tier0Catalog.IsTier0Kind(WatchEventKind.TrustChanged));
        Assert.False(Tier0Catalog.IsTier0Kind(WatchEventKind.UserCreated));
        Assert.False(Tier0Catalog.IsTier0Kind(WatchEventKind.HealthResync));
        Assert.False(Tier0Catalog.IsTier0Kind(WatchEventKind.GroupMemberAdded));
    }

    [Theory]
    [InlineData("userAccountControl", true)]
    [InlineData("USERACCOUNTCONTROL", true)]
    [InlineData("msDS-KeyCredentialLink;binary", true)]
    [InlineData("member", true)]
    [InlineData("nTSecurityDescriptor", true)]
    [InlineData("msDS-LockoutDuration", true)]
    [InlineData("gPLink", true)]
    [InlineData("department", false)]
    [InlineData("extensionAttribute1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTier0Attribute(string? attribute, bool expected) =>
        Assert.Equal(expected, Tier0Catalog.IsTier0Attribute(attribute));

    [Theory]
    [InlineData("tier0-security", true)]
    [InlineData("TIER0-własna", true)]
    [InlineData("tier0", false)]
    [InlineData("lockouts", false)]
    [InlineData(null, false)]
    public void IsTier0Rule(string? id, bool expected) => Assert.Equal(expected, Tier0Catalog.IsTier0Rule(id));

    [Fact]
    public void ProtectedSubtrees_AreTheDomainHeadDomainControllersAndSystem() =>
        Assert.Equal(["{domain}", "OU=Domain Controllers,{domain}", "CN=System,{domain}"], Tier0Catalog.ProtectedSubtrees);
}
