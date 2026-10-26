using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.Tests.PrivilegedAccess;

public class PeerComparisonTests
{
    private const string Ou = "OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test";
    private const string Common = "CN=GG_Ksiegowosc,OU=Grupy,DC=contoso,DC=test";
    private const string Rare = "CN=GG_Płace,OU=Grupy,DC=contoso,DC=test";
    private const string Admins = "CN=GG_Admini,OU=Grupy,DC=contoso,DC=test";

    [Fact]
    public void AGroupHeldByFewerThanTheShareOfAtLeastMinPeers_IsUnusual()
    {
        // Seven accountants: everyone has the common group, Agnieszka and Bożena the rare one, Celina also the admin group.
        var subjects = new List<PeerSubject>
        {
            Subject("agnieszka", Common, Rare),
            Subject("bozena", Common, Rare),
            Subject("celina", Common, Admins),
            Subject("danuta", Common),
            Subject("elzbieta", Common),
            Subject("feliks", Common),
            Subject("grzegorz", Common),
        };

        var result = PeerComparison.Compare(subjects, new PeerOptions(), Granted, Name);

        // Rare: 1 of 6 peers = 16.7 % < 20 %; Admins: 0 of 6.
        Assert.Equal(["agnieszka", "bozena", "celina"], result.Keys.Order(StringComparer.Ordinal));
        var celina = Assert.Single(result["celina"]);
        Assert.Equal(PrivilegeLevel.Elevated, celina.Level);
        Assert.Equal(["GG_Admini"], celina.Values);
        Assert.Equal(6, celina.Peer!.PeerCount);
        var agnieszka = Assert.Single(result["agnieszka"]);
        Assert.Equal(PrivilegeLevel.Minor, agnieszka.Level);
        Assert.Equal(1, Assert.Single(agnieszka.Peer!.Groups).Holders);

        // 15 %: 1 of 6 is no longer below it. Fewer peers than required: no comparison.
        Assert.Equal(["celina"], PeerComparison.Compare(subjects, new PeerOptions { MaxSharePercent = 15 }, Granted, Name).Keys);
        Assert.Empty(PeerComparison.Compare(subjects, new PeerOptions { MinPeers = 7 }, Granted, Name));
        Assert.Empty(PeerComparison.Compare(subjects[..3], new PeerOptions(), Granted, Name));
        Assert.Empty(PeerComparison.Compare(subjects, new PeerOptions { ByOrganizationalUnit = false, ByDepartment = false, ByTitle = false }, Granted, Name));
    }

    [Fact]
    public void Dimensions_TitleAndDepartment_CaseInsensitive_EmptyValuesHaveNoPeers()
    {
        var subjects = Enumerable.Range(0, 6)
            .Select(i => new PeerSubject($"k{i}", $"OU=O{i},DC=contoso,DC=test", i % 2 == 0 ? "Księgowość" : "KSIĘGOWOŚĆ ", "Księgowa", [Common]))
            .Append(new PeerSubject("x", "OU=X,DC=contoso,DC=test", "księgowość", "Księgowa", [Common, Admins]))
            .Append(new PeerSubject("bez", "OU=X,DC=contoso,DC=test", null, " ", [Admins]))
            .ToList();

        var byDepartment = PeerComparison.Compare(subjects, new PeerOptions { ByOrganizationalUnit = false }, Granted, Name);
        var finding = Assert.Single(Assert.Single(byDepartment, kv => kv.Key == "x").Value);
        Assert.Equal(PeerDimension.Department, finding.Peer!.Dimension);
        Assert.Equal(PrivilegeScopeKind.Peers, finding.Scope);
        Assert.Null(finding.ScopeDn);
        Assert.DoesNotContain("bez", byDepartment.Keys);

        var byTitle = PeerComparison.Compare(subjects, new PeerOptions { ByOrganizationalUnit = false, ByDepartment = false, ByTitle = true }, Granted, Name);
        Assert.Equal(PeerDimension.Title, Assert.Single(byTitle["x"]).Peer!.Dimension);
        Assert.Equal(new PeerOptions { MinPeers = 1, MaxSharePercent = 100 }, new PeerOptions { MinPeers = -4, MaxSharePercent = 400 }.Normalized());
    }

    private static PeerSubject Subject(string key, params string[] groups) => new(key, Ou, "Księgowość", "Księgowa", groups);

    private static PrivilegeLevel Granted(string dn) => dn == Admins ? PrivilegeLevel.Tier0 : PrivilegeLevel.None;

    private static string Name(string dn) => dn[3..dn.IndexOf(',', StringComparison.Ordinal)];
}
