using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>Identifier names of created objects (docs/dev/phase3-bulk-create.md 2.2, 2.11).</summary>
public sealed class CreationNamesTests
{
    [Theory]
    [InlineData("kd201", ObjectKind.User, true)]
    [InlineData("jan.kowalski", ObjectKind.User, true)]
    [InlineData("akonstantynopolitanc", ObjectKind.User, true)]
    [InlineData("akonstantynopolitancz", ObjectKind.User, false)]
    [InlineData("jan.kowalski$", ObjectKind.User, true)]
    [InlineData("jan.", ObjectKind.User, false)]
    [InlineData("...", ObjectKind.User, false)]
    [InlineData(". .", ObjectKind.User, false)]
    [InlineData("", ObjectKind.User, false)]
    [InlineData("  ", ObjectKind.User, false)]
    [InlineData("jan@kowalski", ObjectKind.User, false)]
    [InlineData("jan/kowalski", ObjectKind.User, false)]
    [InlineData("jan\tkowalski", ObjectKind.User, false)]
    [InlineData("PC01$", ObjectKind.Computer, true)]
    [InlineData("KOMPUTER-KSIEGO$", ObjectKind.Computer, true)]
    [InlineData("KOMPUTER-KSIEGOW$", ObjectKind.Computer, false)]
    [InlineData("Księgowość Kraków", ObjectKind.Group, true)]
    [InlineData("kd201", ObjectKind.Contact, false)]
    public void IsValidSamAccountName(string value, ObjectKind kind, bool expected) =>
        Assert.Equal(expected, CreationNames.IsValidSamAccountName(value, kind));

    [Fact]
    public void IsValidSamAccountName_GroupsUpTo256() =>
        Assert.Equal((true, false), (CreationNames.IsValidSamAccountName(new string('g', 256), ObjectKind.Group), CreationNames.IsValidSamAccountName(new string('g', 257), ObjectKind.Group)));

    [Theory]
    [InlineData("jan.kowalski", true)]
    [InlineData("kd201", true)]
    [InlineData("łucja.żak", true)]
    [InlineData("jan kowalski", false)]
    [InlineData("jan@kowalski", false)]
    [InlineData("jan(1)", false)]
    [InlineData("a,b", false)]
    [InlineData("a:b", false)]
    [InlineData("a;b", false)]
    [InlineData("a<b>", false)]
    [InlineData("[a]", false)]
    [InlineData("a\\b", false)]
    [InlineData("\"a\"", false)]
    [InlineData("", false)]
    public void IsValidUpnPrefix(string value, bool expected) => Assert.Equal(expected, CreationNames.IsValidUpnPrefix(value));

    [Theory]
    [InlineData("PC01", true)]
    [InlineData("pc-ksiegowosc-1", true)]
    [InlineData("PC_01", true)]
    [InlineData("A", true)]
    [InlineData("KOMPUTER-KSIEGO", true)]
    [InlineData("KOMPUTER-KSIEGOW", false)]
    [InlineData("PC.01", false)]
    [InlineData("-PC01", false)]
    [InlineData("PC01-", false)]
    [InlineData("1234", false)]
    [InlineData("PC 01", false)]
    [InlineData("KSIĘGOWOŚĆ1", false)]
    [InlineData("PC01$", false)]
    [InlineData("", false)]
    public void IsValidComputerName(string value, bool expected) => Assert.Equal(expected, CreationNames.IsValidComputerName(value));

    [Theory]
    [InlineData("Łucja.Żak", "sAMAccountName", "Lucja.Zak")]
    [InlineData("Grzegorz Brzęczyszczykiewicz", "sAMAccountName", "GrzegorzBrzeczyszczykiewicz")]
    [InlineData("jan.\"kowalski\"/[a]:b;c|d=e,f+g*h?i<j>k@l", "sAMAccountName", "jan.kowalskiabcdefghijkl")]
    [InlineData("kowalski...", "sAMAccountName", "kowalski")]
    [InlineData("Ćwikła\tJózef", "sAMAccountName", "CwiklaJozef")]
    [InlineData("José Müller", "sAMAccountName", "JoseMuller")]
    [InlineData("Ж Юрий", "sAMAccountName", "")]
    [InlineData(".lucja..zak.", "mail", "lucja.zak")]
    [InlineData("..jan...kowalski", "userPrincipalName", "jan.kowalski")]
    [InlineData(".lucja..zak", "sAMAccountName", ".lucja..zak")]
    public void SanitizeIdentifier_TheSamFilter(string value, string attribute, string expected) =>
        Assert.Equal(expected, CreationNames.SanitizeIdentifier(value, ObjectKind.User, attribute));

    [Fact]
    public void SanitizeIdentifier_NeverCuts()
    {
        var name = "Konstantynopolitańczykowa.Brzęczyszczykiewicz";

        Assert.Equal("Konstantynopolitanczykowa.Brzeczyszczykiewicz", CreationNames.SanitizeIdentifier(name, ObjectKind.User, "sAMAccountName"));
        Assert.Equal(new string('a', 200), CreationNames.SanitizeIdentifier(new string('a', 200), ObjectKind.User, "mail"));
    }

    [Theory]
    [InlineData("Księgowość / Kadry", "Księgowość Kadry")]
    [InlineData("  Dział IT.  ", "Dział IT")]
    [InlineData("GG_Nowi;Kraków", "GG_NowiKraków")]
    public void SanitizeIdentifier_GroupLogonNames_KeepSpacesAndDiacritics(string value, string expected) =>
        Assert.Equal(expected, CreationNames.SanitizeIdentifier(value, ObjectKind.Group, "sAMAccountName"));

    [Theory]
    [InlineData("sAMAccountName", "jan.kowalski", 2, 20, "jan.kowalski2")]
    [InlineData("sAMAccountName", "akonstantynopolitanc", 2, 20, "akonstantynopolitan2")]
    [InlineData("sAMAccountName", "akonstantynopolitan.", 12, 20, "akonstantynopolita12")]
    [InlineData("sAMAccountName", "abcdefghijklmnopqr.s", 2, 20, "abcdefghijklmnopqr2")]
    [InlineData("userPrincipalName", "jan.kowalski@contoso.com", 2, 1000, "jan.kowalski2@contoso.com")]
    [InlineData("mail", "jan.kowalski@contoso.com", 3, 64, "jan.kowalski3@contoso.com")]
    [InlineData("mail", "jan.kowalski@contoso.com", 2, 10, "jan.kowal2@contoso.com")]
    [InlineData("cn", "Jan Kowalski", 2, 64, "Jan Kowalski (2)")]
    [InlineData("displayName", "Łucja Żak", 3, 64, "Łucja Żak (3)")]
    [InlineData("cn", "Anna Konstantynopolitańczykowa-Brzęczyszczykiewicz", 2, 30, "Anna Konstantynopolitańczy (2)")]
    [InlineData("cn", "Jan Kowalski     x", 2, 17, "Jan Kowalski (2)")]
    public void AppendDisambiguator(string attribute, string value, int number, int maxLength, string expected) =>
        Assert.Equal(expected, CreationNames.AppendDisambiguator(attribute, value, number, maxLength));

    [Fact]
    public void AppendDisambiguator_RejectsImpossibleLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreationNames.AppendDisambiguator("cn", "Jan", 2, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreationNames.AppendDisambiguator("sAMAccountName", "jan", 100, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreationNames.AppendDisambiguator("sAMAccountName", "jan", -1, 20));
        Assert.Equal("12", CreationNames.AppendDisambiguator("sAMAccountName", "jan", 12, 2));
    }
}
