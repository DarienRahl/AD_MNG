using System.Diagnostics;
using AdMng.Presentation.PowerShell;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>
/// The distinguished names of a result that become links (track R, decision D6) - and the ones that must not: a name the
/// engine's table cut, whose beginning is often another valid name.
/// </summary>
public sealed class PowerShellDnLinkFinderTests
{
    private static string[] Found(string text) => [.. PowerShellDnLinkFinder.Find(text).Select(span => span.DistinguishedName)];

    [Theory]
    [InlineData("CN=Jan Kowalski,OU=Sales,DC=contoso,DC=test")]
    [InlineData("CN=Łukasz Kowalski (Kraków),OU=Użytkownicy,OU=Sprzedaż,DC=contoso,DC=test")]
    [InlineData(@"CN=Kowalski\, Jan,OU=Sales,DC=contoso,DC=test")]
    [InlineData("OU=Sales,DC=contoso,DC=test")]
    [InlineData("DC=contoso,DC=test")]
    [InlineData("cn=jan,ou=x,dc=contoso,dc=test")]
    [InlineData("CN=Computers,DC=contoso")]
    public void ANameIsFound_WhateverItsShape(string dn)
    {
        var spans = PowerShellDnLinkFinder.Find($"DistinguishedName : {dn}");

        var span = Assert.Single(spans);
        Assert.Equal(dn, span.DistinguishedName);
        Assert.Equal("DistinguishedName : ".Length, span.Start);
        Assert.Equal(dn.Length, span.Length);
    }

    [Fact]
    public void ARowOfATable_HasItsNameAsTheLastColumn()
    {
        const string Row = "Jan Kowalski  jkowalski  True    CN=Jan Kowalski,OU=Sales,DC=contoso,DC=test";

        Assert.Equal(["CN=Jan Kowalski,OU=Sales,DC=contoso,DC=test"], Found(Row));
    }

    [Fact]
    public void SeveralNamesOnALine_AreFoundInOrder_AListOfThemIsSeparatedByACommaAndASpace()
    {
        Assert.Equal(
            ["CN=A,OU=X,DC=a,DC=b", "CN=B,DC=a,DC=b", "CN=C,DC=a,DC=b"],
            Found("CN=A,OU=X,DC=a,DC=b, CN=B,DC=a,DC=b and CN=C,DC=a,DC=b"));
    }

    [Theory]
    [InlineData("Moved CN=Jan,DC=a,DC=b.", "CN=Jan,DC=a,DC=b")]
    [InlineData("'CN=Jan,DC=a,DC=b'", "CN=Jan,DC=a,DC=b")]
    [InlineData("\"CN=Jan,DC=a,DC=b\"", "CN=Jan,DC=a,DC=b")]
    [InlineData("(CN=Jan,DC=a,DC=b)", "CN=Jan,DC=a,DC=b")]
    [InlineData("@{DistinguishedName=CN=Jan,DC=a,DC=b; Name=Jan}", "CN=Jan,DC=a,DC=b")]
    [InlineData("{CN=Jan,DC=a,DC=b}", "CN=Jan,DC=a,DC=b")]
    [InlineData("member=CN=Jan,DC=a,DC=b", "CN=Jan,DC=a,DC=b")]
    public void PunctuationAroundAName_IsNotPartOfIt(string text, string expected)
    {
        Assert.Equal([expected], Found(text));
    }

    [Theory]
    [InlineData("CN=Łukasz Kowalski,OU=Handlowcy,OU=Użytkownicy,DC=contoso,DC=…")]
    [InlineData("CN=Łukasz Kowalski,OU=Handlowcy,OU=Użytkownicy,DC=contoso,DC=te…")]
    [InlineData("CN=Łukasz Kowalski,OU=Handlowcy,OU=Użytkownic…")]
    [InlineData("CN=Ala,OU=Sales,DC=contoso,DC=test…")]
    public void ANameTheTableCut_IsNoLink_ThePrefixOfANameIsAnotherNameAndTheWrongObject(string text)
    {
        Assert.Empty(PowerShellDnLinkFinder.Find(text));
    }

    /// <summary>
    /// Audit W-06: <c>Format-List</c> wraps a value at the width of the output at any character, and the first line then often ends in a
    /// name that is valid but cut (<c>…,DC=contoso,DC=te</c>, <c>…,DC=contoso,</c>), while the next line starts with the rest
    /// (<c>DC=test</c>, <c>OU=Firma,DC=contoso,DC=test</c>). None of these is a link: each would open another object or none. The lines
    /// are those of the real engine at a width of 120 (lines of 119 characters).
    /// </summary>
    [Theory]
    [InlineData("DistinguishedNameOfTheObjectInTheDirectory : CN=Małgorzata Wójcik,OU=Księgowość,OU=Pracownicy,OU=Firma,DC=contoso,DC=te")]
    [InlineData("DistinguishedNameOfTheObjectInTheDirectoryX : CN=Małgorzata Wójcik,OU=Księgowość,OU=Pracownicy,OU=Firma,DC=contoso,DC=t")]
    [InlineData("DistinguishedNameOfTheObjectInTheDirectoryXXXXXXXXXXX : CN=Małgorzata Wójcik,OU=Księgowość,OU=Pracownicy,OU=Firma,DC=co")]
    [InlineData("DistinguishedNameOfTheObjectInTheDirectoryXX : CN=Małgorzata Wójcik,OU=Księgowość,OU=Pracownicy,OU=Firma,DC=contoso,")]
    [InlineData("member : {CN=Łukasz Wiśniewski,OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,")]
    [InlineData("                                             DC=test")]
    [InlineData("                                             st")]
    [InlineData("         OU=Firma,DC=contoso,DC=test}")]
    [InlineData("                                             DC=contoso,DC=test")]
    public void ANameThatFormatListWrapped_IsNoLink(string line)
    {
        Assert.Empty(PowerShellDnLinkFinder.Find(line, width: 120));
    }

    [Fact]
    public void ANameThatEndsWithTheLine_IsStillALink_WhenTheLineIsShorterThanTheWidth_AndOtherNamesOfAWrappedLineStay()
    {
        Assert.Equal(["CN=Jan,OU=IT,DC=contoso,DC=test"], Found("DistinguishedName : CN=Jan,OU=IT,DC=contoso,DC=test"));
        Assert.Equal(
            ["CN=Jan Kowalski,OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=test"],
            [.. PowerShellDnLinkFinder.Find("member : {CN=Jan Kowalski,OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=test, CN=Łukasz Wiśniewski,OU=IT,OU=Pracownicy,OU=", width: 120).Select(span => span.DistinguishedName)]);
        Assert.Equal(
            ["CN=Jan Kowalski,OU=IT,DC=contoso,DC=test"],
            [.. PowerShellDnLinkFinder.Find("member : {CN=Jan Kowalski,OU=IT,DC=contoso,DC=test, CN=Łukasz Wiśniewski,OU=IT,DC=contoso,", width: 120).Select(span => span.DistinguishedName)]);
        Assert.Equal(["DC=contoso,DC=test"], Found("DC=contoso,DC=test"));
    }

    [Theory]
    [InlineData("xCN=Jan,DC=a,DC=b")]
    [InlineData(@"C:\CN=Jan,DC=a,DC=b")]
    [InlineData("CN=Jan,OU=Sales")]
    [InlineData("CN=Jan")]
    [InlineData("just text, no name = here")]
    [InlineData("DC=")]
    [InlineData("")]
    public void NotAName_IsNotFound(string text)
    {
        Assert.Empty(PowerShellDnLinkFinder.Find(text));
    }

    [Fact]
    public void NullAndTooLongText_AreNothing()
    {
        Assert.Empty(PowerShellDnLinkFinder.Find(null));
        Assert.Empty(PowerShellDnLinkFinder.Find("CN=Jan,DC=a,DC=b " + new string(' ', PowerShellDnLinkFinder.MaxTextLength)));
    }

    [Fact]
    public void HostileText_IsReadInTimeAndNeverHangsTheWindow()
    {
        var hostile = string.Concat(Enumerable.Repeat("CN=a,", 1_500)) + "x";
        var commas = "CN=" + new string(',', 7_000);
        var backslashes = "CN=" + string.Concat(Enumerable.Repeat(@"\", 7_000)) + ",DC=a";

        var watch = Stopwatch.StartNew();
        _ = PowerShellDnLinkFinder.Find(hostile);
        _ = PowerShellDnLinkFinder.Find(commas);
        _ = PowerShellDnLinkFinder.Find(backslashes);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }
}
