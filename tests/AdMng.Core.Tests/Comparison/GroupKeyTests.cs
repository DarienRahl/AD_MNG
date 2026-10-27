using AdMng.Core.Comparison;

namespace AdMng.Core.Tests.Comparison;

public class GroupKeyTests
{
    private const string ContosoSid = "S-1-5-21-3623811015-3361044348-30300820";
    private const string FabrikamSid = "S-1-5-21-1004336348-1177238915-682003330";

    [Fact]
    public void ABuiltinGroup_IsMatchedByItsSid()
    {
        Assert.Equal("sid:S-1-5-32-544", GroupKey.For("S-1-5-32-544", "Administrators", "Administrators"));
        Assert.Equal(GroupKey.For("S-1-5-32-544", "Administrators", "Administrators"), GroupKey.For("S-1-5-32-544", "Administratorzy", "Administratorzy"));
    }

    [Fact]
    public void AWellKnownDomainGroup_IsMatchedByItsRid_WhateverTheLanguageOfItsName()
    {
        var english = GroupKey.For($"{ContosoSid}-513", "Domain Users", "Domain Users");
        var polish = GroupKey.For($"{FabrikamSid}-513", "Użytkownicy domeny", "Użytkownicy domeny");

        Assert.Equal("rid:513", english);
        Assert.Equal(english, polish);
        Assert.Equal("rid:525", GroupKey.For($"{FabrikamSid}-525", "Protected Users", "Protected Users"));
    }

    [Fact]
    public void AnOrdinaryGroup_IsMatchedBySamAccountName_CaseInsensitively()
    {
        Assert.Equal(GroupKey.For($"{ContosoSid}-1105", "GG_Sprzedaż", "GG_Sprzedaż"), GroupKey.For($"{FabrikamSid}-2201", "gg_sprzedaż", "Sprzedaż (Fabrikam)"));
        Assert.NotEqual(GroupKey.For($"{ContosoSid}-1105", "GG_IT", "GG_IT"), GroupKey.For($"{FabrikamSid}-1105", "GG_Księgowość", "GG_IT"));
    }

    [Fact]
    public void WithoutSamAccountName_TheNameIsTheKey()
    {
        Assert.Equal("name:GRUPA ŁĄCZNOŚCI", GroupKey.For(null, null, "Grupa łączności"));
        Assert.Equal(string.Empty, GroupKey.For(null, " ", null));
    }

    [Fact]
    public void Principals_FallBackToMailThenName()
    {
        Assert.Equal("sam:JAN.KOWALSKI", GroupKey.ForPrincipal($"{ContosoSid}-1103", "jan.kowalski", "jan.kowalski@contoso.test", "Jan Kowalski"));
        Assert.Equal("rid:500", GroupKey.ForPrincipal($"{ContosoSid}-500", "Administrator", null, "Administrator"));
        Assert.Equal("mail:ŻANETA.KONTAKT@PARTNER.TEST", GroupKey.ForPrincipal(null, null, "żaneta.kontakt@partner.test", "Żaneta Kontakt"));
        Assert.Equal("name:ŻANETA KONTAKT", GroupKey.ForPrincipal(null, null, null, "Żaneta Kontakt"));
    }
}
