using AdMng.Core.Correspondence.Mailboxes;

namespace AdMng.Core.Tests.Correspondence.Mailboxes;

/// <summary>The folded matching key of person names.</summary>
public class PersonNameKeyTests
{
    [Theory]
    [InlineData("Łukasz Wiśniewski", "lukasz wisniewski")]
    [InlineData("  ŁUKASZ   WIŚNIEWSKI  ", "lukasz wisniewski")]
    [InlineData("Agnieszka Żółć", "agnieszka zolc")]
    [InlineData("ĄĆĘŁŃÓŚŹŻ ąćęłńóśźż", "acelnoszz acelnoszz")]
    [InlineData("Kowalska-Nowak", "kowalska nowak")]
    [InlineData("Kowalska - Nowak", "kowalska nowak")]
    [InlineData("Kowalska – Nowak", "kowalska nowak")]
    [InlineData("Kowalska—Nowak", "kowalska nowak")]
    [InlineData("Kowalska--Nowak", "kowalska nowak")]
    [InlineData("Kowalski, Jan", "kowalski jan")]
    [InlineData("-Kowalski-", "kowalski")]
    [InlineData("O'Neill", "o'neill")]
    [InlineData("O’Neill", "o'neill")]
    [InlineData("Müller", "muller")]
    [InlineData("Jan M. Kowalski", "jan m. kowalski")]
    public void Fold_RemovesDiacritics_LowerCases_AndCollapsesSeparators(string name, string expected) =>
        Assert.Equal(expected, PersonNameKey.Fold(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData(" - , – ")]
    public void Fold_OfNothing_IsEmpty(string? name) => Assert.Equal(string.Empty, PersonNameKey.Fold(name));

    [Fact]
    public void Fold_DropsInvisibleCharacters_AndComposesDecomposedLetters()
    {
        var softHyphen = (char)0x00AD;
        var zeroWidthSpace = (char)0x200B;
        var combiningAcute = (char)0x0301;
        var nonBreakingSpace = (char)0x00A0;

        Assert.Equal("kowalski", PersonNameKey.Fold($"Ko{softHyphen}wal{zeroWidthSpace}ski"));
        Assert.Equal("wojcik", PersonNameKey.Fold($"Wo{combiningAcute}jcik"));
        Assert.Equal("jan kowalski", PersonNameKey.Fold($"Jan{nonBreakingSpace}Kowalski"));
        Assert.Equal("jankowalski", PersonNameKey.Fold($"Jan{(char)1}Kowalski"));
    }

    [Fact]
    public void Fold_IsIdempotent_AndIgnoresCaseAndDiacriticsAlike()
    {
        string[] names = ["Grażyna Dąbrowska", "GRAZYNA DABROWSKA", "grazyna  dabrowska", "Grażyna-Dąbrowska"];
        var keys = names.Select(PersonNameKey.Fold).Distinct().ToList();

        Assert.Equal("grazyna dabrowska", Assert.Single(keys));
        Assert.Equal(keys[0], PersonNameKey.Fold(keys[0]));
    }
}
