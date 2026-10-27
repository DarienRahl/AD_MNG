using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.Tests.PrivilegedAccess;

public class PrivilegedGroupCatalogTests
{
    private const string Domain = "S-1-5-21-100-200-300";
    private const string Root = "S-1-5-21-900-800-700";

    [Theory]
    [InlineData("S-1-5-32-544", null, "Administrators", PrivilegeLevel.Tier0)]
    [InlineData("S-1-5-32-548", null, "AccountOperators", PrivilegeLevel.Tier0)]
    [InlineData("S-1-5-32-555", null, "RemoteDesktopUsers", PrivilegeLevel.Elevated)]
    [InlineData("S-1-5-32-573", null, "EventLogReaders", PrivilegeLevel.Minor)]
    [InlineData(Domain + "-512", null, "DomainAdmins", PrivilegeLevel.Tier0)]
    [InlineData(Domain + "-520", null, "GroupPolicyCreatorOwners", PrivilegeLevel.High)]
    [InlineData(Domain + "-1105", "DnsAdmins", "DnsAdmins", PrivilegeLevel.Tier0)]
    [InlineData(Domain + "-1106", "dnsadmins", "DnsAdmins", PrivilegeLevel.Tier0)]
    public void Match_BySidRidOrUnlocalizedName(string sid, string? sam, string id, PrivilegeLevel level)
    {
        var group = PrivilegedGroupCatalog.Match(sid, sam, Domain, Root);

        Assert.NotNull(group);
        Assert.Equal(id, group.Id);
        Assert.Equal(level, group.DefaultLevel);
    }

    [Fact]
    public void Match_NeverByTheLocalizedName_AndNotTheRidOfAnotherDomain()
    {
        // "Administratorzy domeny" of a Polish domain is Domain Admins only by its RID; a custom group named like it is not.
        Assert.Null(PrivilegedGroupCatalog.Match(Domain + "-1200", "Administratorzy domeny", Domain, Root));
        Assert.Null(PrivilegedGroupCatalog.Match(Domain + "-1201", "Domain Admins", Domain, Root));
        Assert.Null(PrivilegedGroupCatalog.Match("S-1-5-21-5-6-7-512", "Domain Admins", Domain, Root));
        Assert.Null(PrivilegedGroupCatalog.Match(null, null, Domain, Root));
        Assert.Null(PrivilegedGroupCatalog.Match("S-1-5-32-545", "Użytkownicy", Domain, Root));
    }

    [Fact]
    public void ForestRootGroups_MatchInTheRootDomain_OrAnywhereWhileItIsUnknown()
    {
        Assert.Equal("EnterpriseAdmins", PrivilegedGroupCatalog.Match(Root + "-519", null, Domain, Root)?.Id);
        Assert.Null(PrivilegedGroupCatalog.Match(Domain + "-519", null, Domain, Root));
        Assert.Equal("SchemaAdmins", PrivilegedGroupCatalog.Match(Domain + "-518", null, Domain, rootDomainSid: null)?.Id);
        Assert.Equal([518u, 519u, 527u], PrivilegedGroupCatalog.ForestRootTier0Rids.Order());
        Assert.Equal($"{Root}-519", PrivilegedGroupCatalog.Find("EnterpriseAdmins")!.SidIn(Domain, Root));
        Assert.Null(PrivilegedGroupCatalog.Find("EnterpriseAdmins")!.SidIn(Domain, null));
        Assert.Equal($"{Domain}-512", PrivilegedGroupCatalog.Find("DomainAdmins")!.SidIn(Domain, Root));
        Assert.Null(PrivilegedGroupCatalog.Find("DnsAdmins")!.SidIn(Domain, Root));
    }

    [Fact]
    public void Catalogue_HasUniqueIds_EveryLevel_AndTheProtectedGroupsOfAdminSdHolder()
    {
        Assert.Equal(PrivilegedGroupCatalog.All.Count, PrivilegedGroupCatalog.All.Select(g => g.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(PrivilegedGroupCatalog.All, g => Assert.True(g.BuiltinSid is not null ^ g.Rid is not null ^ g.SamAccountName is not null));
        Assert.Equal(
            [PrivilegeLevel.Minor, PrivilegeLevel.Elevated, PrivilegeLevel.High, PrivilegeLevel.Tier0],
            PrivilegedGroupCatalog.All.Select(g => g.DefaultLevel).Distinct().Order());
        var protectedIds = PrivilegedGroupCatalog.All.Where(g => g.IsProtected).Select(g => g.Id).ToHashSet();
        Assert.Superset(
            new HashSet<string> { "Administrators", "DomainAdmins", "EnterpriseAdmins", "SchemaAdmins", "AccountOperators", "ServerOperators", "PrintOperators", "BackupOperators", "Replicator", "DomainControllers", "ReadOnlyDomainControllers", "KeyAdmins", "EnterpriseKeyAdmins" },
            protectedIds);

        // The account review (U2) uses the SID/RID groups that are protected or at least High.
        Assert.DoesNotContain(PrivilegedGroupCatalog.ProtectedOrHigh, g => g.SamAccountName is not null);
        Assert.Contains(PrivilegedGroupCatalog.ProtectedOrHigh, g => g.Id == "Replicator");
        Assert.DoesNotContain(PrivilegedGroupCatalog.ProtectedOrHigh, g => g.Id == "RemoteDesktopUsers");
    }

    [Fact]
    public void Levels_MaxAndNormalize()
    {
        Assert.Equal(PrivilegeLevel.High, PrivilegeLevels.Max([PrivilegeLevel.Minor, PrivilegeLevel.High, PrivilegeLevel.Elevated]));
        Assert.Equal(PrivilegeLevel.None, PrivilegeLevels.Max([]));
        Assert.Equal(PrivilegeLevel.Minor, PrivilegeLevels.Normalize(PrivilegeLevel.None));
        Assert.Equal(PrivilegeLevel.Minor, PrivilegeLevels.Normalize((PrivilegeLevel)42));
        Assert.Equal(PrivilegeLevel.Tier0, PrivilegeLevels.Normalize(PrivilegeLevel.Tier0));
        Assert.Equal(PrivilegeLevel.Tier0, PrivilegeLevels.Descending[0]);
    }
}
