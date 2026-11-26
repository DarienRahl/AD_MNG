using AdMng.Core.PowerShell;

namespace AdMng.Core.Tests.PowerShell;

/// <summary>The plain table <c>Out-AdmGrid</c> and <c>Export-AdmList</c> hand to the window (track R, decision D6).</summary>
public sealed class PowerShellGridTableTests
{
    private static PowerShellGridTable Table() =>
        new(["DistinguishedName", "Name", "mail"], [["CN=Jan,DC=a,DC=b", "Jan", "jan@a.b"], ["CN=Ola,DC=a,DC=b", "Ola", null]], "Users", "distinguishedname");

    [Fact]
    public void ATable_IsPlainData_WithTheColumnOfDistinguishedNames()
    {
        var table = Table();

        Assert.Equal(["DistinguishedName", "Name", "mail"], table.Columns);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Ola", table.Rows[1][1]);
        Assert.Null(table.Rows[1][2]);
        Assert.Equal("Users", table.Title);
        Assert.Equal(0, table.DistinguishedNameColumn);
    }

    [Fact]
    public void ATable_WithoutADnColumn_HasNone_AndABlankTitleIsNoTitle()
    {
        var table = new PowerShellGridTable(["Name"], [["a"]], "  ");

        Assert.Equal(-1, table.DistinguishedNameColumn);
        Assert.Null(table.Title);
        Assert.Empty(new PowerShellGridTable(["Name"], []).Rows);
    }

    [Fact]
    public void ATable_CopiesWhatItWasGiven_SoTheCallersListsCanChange()
    {
        var columns = new List<string> { "Name" };
        var rows = new List<IReadOnlyList<string?>> { new List<string?> { "a" } };

        var table = new PowerShellGridTable(columns, rows);
        columns.Add("Other");
        rows.Clear();

        Assert.Equal(["Name"], table.Columns);
        Assert.Single(table.Rows);
    }

    [Theory]
    [InlineData("a", "A")]
    [InlineData("a", "")]
    [InlineData("a", " ")]
    public void ATable_RefusesAColumnThatHasNoNameOrTwo(string first, string second)
    {
        Assert.Throws<ArgumentException>(() => new PowerShellGridTable([first, second], []));
    }

    [Fact]
    public void ATable_RefusesARowOfAnotherLength_AndADnColumnThatIsNotOne()
    {
        Assert.Throws<ArgumentException>(() => new PowerShellGridTable(["a", "b"], [["x"]]));
        Assert.Throws<ArgumentException>(() => new PowerShellGridTable(["a"], [["x", "y"]]));
        Assert.Throws<ArgumentException>(() => new PowerShellGridTable(["a"], [["x"]], null, "dn"));
        Assert.Throws<ArgumentNullException>(() => new PowerShellGridTable(null!, []));
    }

    [Fact]
    public void ATableShowsNothingOfItsValues_InToString()
    {
        var text = Table().ToString();

        Assert.DoesNotContain("Jan", text, StringComparison.Ordinal);
        Assert.Equal("PowerShellGridTable(3 columns, 2 rows)", text);
    }
}
