using AdMng.Core.Batch.Creation;
using AdMng.Core.Messaging.Html;
using AdMng.Testing.BulkCreate;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>The HTML table of the clipboard and of <c>.html</c> files (docs/dev/phase3-bulk-create.md 2.1).</summary>
public sealed class HtmlTableReaderTests
{
    private static HtmlTable Read(string html) =>
        HtmlTableReader.Read(HtmlNesting.ParseDocument(html) ?? throw new InvalidOperationException("Not parsed.")) ?? throw new InvalidOperationException("No table.");

    [Fact]
    public void Excel_ReadsTheCells_AndALinkIsItsText()
    {
        var table = Read(ExcelSamples.ExcelHtml);

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(["Imię", "Nazwisko", "E-mail"], table.Rows[0].Select(c => c.Trim()));
        Assert.Equal("Żak", table.Rows[1][1].Trim());
        Assert.DoesNotContain(table.Rows.SelectMany(r => r), c => c.Contains("mailto", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(table.MergedRows);
        Assert.Equal(0, table.DroppedColumns);
    }

    [Fact]
    public void Excel_CarriesTheFullNumber()
    {
        var table = Read(ExcelSamples.ScientificHtml);

        Assert.Equal("4,86E+11", table.Rows[1][2].Trim());
        Assert.Equal(ExcelSamples.ScientificFullValue, table.Numbers[1][2]);
        Assert.Null(table.Numbers[2][2]);
        Assert.Null(table.Numbers[1][0]);
    }

    [Theory]
    [InlineData("<table><tr><td x:num=\"4.86001002001E+11\">4,86E+11</td></tr></table>", "486001002001")]
    [InlineData("<table><tr><td x:num=\"486001002001\">4,86E+11</td></tr></table>", "486001002001")]
    [InlineData("<table><tr><td x:num=\"3.5\">3,5</td></tr></table>", null)]
    [InlineData("<table><tr><td x:num=\"-12\">-12</td></tr></table>", null)]
    [InlineData("<table><tr><td x:num>12</td></tr></table>", null)]
    public void XNum_OnlyIntegers(string html, string? number) => Assert.Equal(number, Read(html).Numbers[0][0]);

    [Fact]
    public void LibreOffice_LineBreaksAndColspan()
    {
        var table = Read(ExcelSamples.LibreOfficeHtml);

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("ul. Brzozowa 8\r\n00-950 Warszawa", table.Rows[1][2]);
        Assert.Equal(["Witold Świątek", "", "ul. Sosnowa 9"], table.Rows[2]);
        Assert.Equal([2], table.MergedRows);
    }

    [Fact]
    public void GoogleSheets_PlainTable()
    {
        var table = Read(ExcelSamples.GoogleSheetsHtml);

        Assert.Equal([["Imię", "Nazwisko", "E-mail"], ["Celina", "Żuraw", "celina.zuraw@contoso.com"], ["Bogdan", "Ćmiel", "bogdan.cmiel@contoso.com"]], table.Rows);
    }

    [Fact]
    public void MergedAndNested_TheTableWithMostRows_RowspanAndNestedTableSkipped()
    {
        var table = Read(ExcelSamples.MergedAndNestedHtml);

        Assert.Equal(4, table.Rows.Count);
        Assert.Equal(["Imię", "Nazwisko", "Dział", "Uwagi"], table.Rows[0]);
        Assert.Equal(["Hanna", "Łęcka", "Księgowość", "od 1 grudnia"], table.Rows[1]);

        // The rowspan covers Dział of the next row: its own last cell is Uwagi, not Dział.
        Assert.Equal(["Witold", "Świątek", "", ""], table.Rows[2]);
        Assert.Equal("bez zmian", table.Rows[3][3]);
        Assert.Equal([1], table.MergedRows);
    }

    [Fact]
    public void Rowspan_ToTheRightOfAShortRow_IsFilled()
    {
        var table = Read("<table><tr><td>a</td><td rowspan=3>b</td></tr><tr><td>c</td></tr><tr></tr><tr><td>d</td><td>e</td></tr></table>");

        Assert.Equal([["a", "b"], ["c", ""], ["", ""], ["d", "e"]], table.Rows);
    }

    [Fact]
    public void BlocksAndIgnoredElements()
    {
        var table = Read("<table><tr><td><p>ul. Długa 1</p><p>00-950 Warszawa</p></td><td>Ewa<script>alert(1)</script><style>td{}</style></td></tr></table>");

        Assert.Equal("ul. Długa 1\r\n00-950 Warszawa", table.Rows[0][0].TrimEnd());
        Assert.Equal("Ewa", table.Rows[0][1]);
    }

    [Fact]
    public void TheadTbodyTfoot_AreRowsOfTheTable()
    {
        var table = Read("<table><thead><tr><th>Imię</th></tr></thead><tbody><tr><td>Ewa</td></tr></tbody><tfoot><tr><td>Razem</td></tr></tfoot></table>");

        Assert.Equal([["Imię"], ["Ewa"], ["Razem"]], table.Rows);
    }

    [Fact]
    public void ATableInATemplate_IsIgnored_AndNoTable_IsNull()
    {
        var template = HtmlNesting.ParseDocument("<template><table><tr><td>x</td></tr><tr><td>y</td></tr></table></template><p>tekst</p>")!;
        var none = HtmlNesting.ParseDocument("<p>Łucja Żak</p>")!;

        Assert.Null(HtmlTableReader.Read(template));
        Assert.Null(HtmlTableReader.Read(none));
    }

    [Fact]
    public void Colspan_IsCappedAtTheColumnLimit()
    {
        var table = Read($"<table><tr><td colspan=\"100000\">a</td><td>b</td></tr></table>");

        Assert.Equal(TableTextReader.MaxColumns, table.Rows[0].Count);
        Assert.Equal(1, table.DroppedColumns);
    }

    [Fact]
    public void ToString_CountsOnly() => Assert.Equal("HTML table: 3 rows", Read(ExcelSamples.GoogleSheetsHtml).ToString());
}
