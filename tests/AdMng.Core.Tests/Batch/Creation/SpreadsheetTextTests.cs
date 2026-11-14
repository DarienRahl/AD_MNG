using AdMng.Core.Batch.Creation;
using AdMng.Testing.BulkCreate;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>Excel's TSV quoting rule of the clipboard text (docs/dev/phase3-bulk-create.md 2.1).</summary>
public sealed class SpreadsheetTextTests
{
    [Fact]
    public void QuotesTsv_FollowsExcelsQuotingRule()
    {
        var lines = SpreadsheetText.SplitTsv(ExcelSamples.QuotesTsv);

        Assert.Equal(1 + ExcelSamples.QuotesRowCount, lines.Count);
        Assert.Equal("\"Społem\" PSS", lines[1][2]);
        Assert.Equal("ul. Kwiatowa 5\n00-950 Warszawa", lines[2][3]);
        Assert.Equal("ok", lines[2][4]);
        Assert.Equal("Firma \"Bis\"\nOddział 2", lines[3][2]);
        Assert.Equal("Dział\tA", lines[4][2]);
        Assert.Equal(["\"Piotr", "Wójcik", "Contoso", "ul. Leśna 7", "bez zamknięcia cudzysłowu"], lines[5]);
        Assert.Equal(["Hanna", "Łęcka", "Contoso", "ul. Brzozowa 8", ""], lines[6]);
        Assert.Equal(["Bogdan", "Ćmiel", "Contoso", "ul. Bukowa 11", ""], lines[9]);
        Assert.All(lines, l => Assert.Equal(5, l.Count));
    }

    [Theory]
    [InlineData("a\tb\r\nc\td\r\n")]
    [InlineData("a\tb\nc\td\n")]
    [InlineData("a\tb\rc\td\r")]
    [InlineData("a\tb\r\nc\td")]
    [InlineData("a\tb\nc\td")]
    public void LineEndings_AndAMissingFinalBreak(string text)
    {
        var lines = SpreadsheetText.SplitTsv(text);

        Assert.Equal(2, lines.Count);
        Assert.Equal(["a", "b"], lines[0]);
        Assert.Equal(["c", "d"], lines[1]);
    }

    [Fact]
    public void CrOnlyAndLfOnlySamples()
    {
        Assert.Equal(3, SpreadsheetText.SplitTsv(ExcelSamples.CrOnlyTsv).Count);
        Assert.Equal(["Grzegorz", "Bąk", "grzegorz.bak@contoso.com"], SpreadsheetText.SplitTsv(ExcelSamples.LfOnlyTsv)[2]);
    }

    [Theory]
    [InlineData("\"\"\tx", "", "x")]
    [InlineData("\"\"\"Bis\"\"\"\tx", "\"Bis\"", "x")]
    [InlineData("\"a\"b\tx", "\"a\"b", "x")]
    [InlineData("a\"b\"\tx", "a\"b\"", "x")]
    [InlineData("\"\tx", "\"", "x")]
    [InlineData("\"a\r\nb\"\tx", "a\r\nb", "x")]
    public void QuotedCells(string text, string first, string second) => Assert.Equal([first, second], SpreadsheetText.SplitTsv(text)[0]);

    [Fact]
    public void AQuoteAtTheEndOfTheText_ClosesTheCell() => Assert.Equal(["a\tb"], SpreadsheetText.SplitTsv("\"a\tb\"")[0]);

    [Fact]
    public void EmptyCellsAndLines_AreKept()
    {
        var lines = SpreadsheetText.SplitTsv("a\t\t\r\n\r\n\tb\r\n");

        Assert.Equal(3, lines.Count);
        Assert.Equal(["a", "", ""], lines[0]);
        Assert.Equal([""], lines[1]);
        Assert.Equal(["", "b"], lines[2]);
        Assert.Empty(SpreadsheetText.SplitTsv(string.Empty));
    }

    [Fact]
    public void AnUnbalancedQuote_DoesNotSwallowRowsWithQuotedCellsLater()
    {
        var text = "\"Piotr\tWójcik\r\nHanna\t\"Łęcka\"\r\nWitold\t\"Świątek\r\nul. 9\"\r\n";

        var lines = SpreadsheetText.SplitTsv(text);

        Assert.Equal(3, lines.Count);
        Assert.Equal(["\"Piotr", "Wójcik"], lines[0]);
        Assert.Equal(["Hanna", "Łęcka"], lines[1]);
        Assert.Equal(["Witold", "Świątek\r\nul. 9"], lines[2]);
    }

    [Fact]
    public void HasTabInFirstLines()
    {
        Assert.True(SpreadsheetText.HasTabInFirstLines(ExcelSamples.PolishTsv, 20));
        Assert.False(SpreadsheetText.HasTabInFirstLines(ExcelSamples.SingleColumnWebText, 20));
        Assert.False(SpreadsheetText.HasTabInFirstLines(string.Concat(Enumerable.Repeat("x\r\n", 20)) + "a\tb", 20));
        Assert.True(SpreadsheetText.HasTabInFirstLines(string.Concat(Enumerable.Repeat("x\r\n", 19)) + "a\tb", 20));
    }

    [Fact]
    public void ContainsScientificCell()
    {
        Assert.True(SpreadsheetText.ContainsScientificCell(ExcelSamples.ScientificTsv));
        Assert.True(SpreadsheetText.ContainsScientificCell("a\t 1E5 "));
        Assert.False(SpreadsheetText.ContainsScientificCell(ExcelSamples.PolishTsv));
        Assert.False(SpreadsheetText.ContainsScientificCell("Ewa\tE+11\r\n"));
        Assert.False(SpreadsheetText.ContainsScientificCell(string.Empty));
    }
}
