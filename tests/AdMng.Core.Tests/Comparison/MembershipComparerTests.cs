using AdMng.Core.Comparison;

namespace AdMng.Core.Tests.Comparison;

public class MembershipComparerTests
{
    [Fact]
    public void Groups_AreMatchedByKey_WithTheirKindOnEachSide()
    {
        MembershipEntry[] a =
        [
            new("rid:513", "Domain Users", "CN=Domain Users,CN=Users,DC=contoso,DC=test", MembershipKind.Primary),
            new("sam:GG_IT", "GG_IT", "CN=GG_IT,OU=Grupy,DC=contoso,DC=test", MembershipKind.Direct),
            new("sam:DL_WSZYSCY", "DL_Wszyscy", "CN=DL_Wszyscy,OU=Grupy,DC=contoso,DC=test", MembershipKind.Nested),
        ];
        MembershipEntry[] b =
        [
            new("rid:513", "Użytkownicy domeny", "CN=Użytkownicy domeny,CN=Users,DC=fabrikam,DC=test", MembershipKind.Primary),
            new("sam:GG_SPRZEDAZ", "GG_Sprzedaz", "CN=GG_Sprzedaz,OU=Grupy,DC=fabrikam,DC=test", MembershipKind.Direct),
            new("sam:DL_WSZYSCY", "DL_Wszyscy", "CN=DL_Wszyscy,OU=Grupy,DC=fabrikam,DC=test", MembershipKind.Direct),
        ];

        var rows = MembershipComparer.Compare(a, b).ToDictionary(r => r.Key);

        Assert.Equal(MembershipComparisonState.InBoth, rows["rid:513"].State);
        Assert.Equal("Domain Users", rows["rid:513"].A!.Name);
        Assert.Equal("Użytkownicy domeny", rows["rid:513"].B!.Name);
        Assert.False(rows["rid:513"].IsDifference);
        Assert.Equal(MembershipComparisonState.OnlyA, rows["sam:GG_IT"].State);
        Assert.Equal(MembershipComparisonState.OnlyB, rows["sam:GG_SPRZEDAZ"].State);
        Assert.Equal(MembershipComparisonState.InBothDifferently, rows["sam:DL_WSZYSCY"].State);
        Assert.True(rows["sam:DL_WSZYSCY"].IsDifference);
    }

    [Fact]
    public void AGroupListedTwiceOnOneSide_CountsOnce_AsItsStrongestKind()
    {
        MembershipEntry[] a =
        [
            new("sam:GG_IT", "GG_IT", "CN=GG_IT,DC=contoso,DC=test", MembershipKind.Nested),
            new("sam:GG_IT", "GG_IT", "CN=GG_IT,DC=contoso,DC=test", MembershipKind.Direct),
        ];
        MembershipEntry[] b = [new("sam:GG_IT", "GG_IT", "CN=GG_IT,DC=fabrikam,DC=test", MembershipKind.Direct)];

        var row = Assert.Single(MembershipComparer.Compare(a, b));

        Assert.Equal(MembershipComparisonState.InBoth, row.State);
        Assert.Equal(MembershipKind.Direct, row.A!.Kind);
    }

    [Fact]
    public void EntriesWithoutAKey_AreNeverMatchedAcrossSides()
    {
        MembershipEntry[] a = [new(string.Empty, "Bez nazwy", "CN=X,DC=contoso,DC=test", MembershipKind.Direct)];
        MembershipEntry[] b = [new(string.Empty, "Bez nazwy", "CN=X,DC=contoso,DC=test", MembershipKind.Direct)];

        var rows = MembershipComparer.Compare(a, b);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.State == MembershipComparisonState.OnlyA);
        Assert.Contains(rows, r => r.State == MembershipComparisonState.OnlyB);
        Assert.DoesNotContain("Bez nazwy", rows[0].ToString(), StringComparison.Ordinal);
    }
}
