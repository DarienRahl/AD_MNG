using AdMng.Core.Csv;

namespace AdMng.Core.Tests.Csv;

public class CsvCodecTests
{
    private readonly CsvCodec _codec = new();

    private CsvTable Read(string text, char? delimiter = null) => _codec.Read(new StringReader(text), delimiter);

    private string Write(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, char delimiter = ';', bool protect = true)
    {
        using var writer = new StringWriter();
        _codec.Write(writer, headers, rows, delimiter, protect);
        return writer.ToString();
    }

    [Fact]
    public void Read_PolishExcelFileWithBom()
    {
        var table = Read("﻿sAMAccountName;Dział;Miasto\r\njan.kowalski;Księgowość;Łódź\r\nżaneta.s;Sprzedaż;Gdańsk\r\n");

        Assert.Equal(';', table.Delimiter);
        Assert.Equal(["sAMAccountName", "Dział", "Miasto"], table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Łódź", table.Cell(0, 2));
        Assert.Equal("żaneta.s", table.Cell(1, 0));
        Assert.Equal(1, table.IndexOf("dział"));
    }

    [Theory]
    [InlineData("a,b,c\n1,2,3", ',')]
    [InlineData("a\tb\tc\n1\t2\t3", '\t')]
    [InlineData("a;b;c\n1;2;3", ';')]
    [InlineData("\"x;y\",b,c\n1,2,3", ',')]
    [InlineData("\"a,b\";c\n1;2", ';')]
    [InlineData("single\nvalue", ';')]
    [InlineData("\n\n  \na,b\n1,2", ',')]
    public void Read_DetectsDelimiterFromHeader(string text, char expected)
    {
        Assert.Equal(expected, Read(text).Delimiter);
    }

    [Fact]
    public void Read_ExplicitDelimiterOverridesDetection()
    {
        var table = Read("a;b,c\n1;2,3", ',');

        Assert.Equal(["a;b", "c"], table.Headers);
        Assert.Equal(["1;2", "3"], table.Rows[0]);
    }

    [Fact]
    public void Read_HandlesQuotedFieldsWithDelimitersQuotesAndNewLines()
    {
        var text = "name;description;city\r\n\"Kowalski; Jan\";\"Powiedział \"\"cześć\"\"\";\"Linia 1\r\nLinia 2\nLinia 3\"\r\n\"\";x;\r\n";

        var table = Read(text);

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Kowalski; Jan", table.Cell(0, 0));
        Assert.Equal("Powiedział \"cześć\"", table.Cell(0, 1));
        Assert.Equal("Linia 1\r\nLinia 2\nLinia 3", table.Cell(0, 2));
        Assert.Equal(["", "x", ""], table.Rows[1]);
    }

    [Fact]
    public void Read_AcceptsAllLineEndingsAndMissingTrailingNewLine()
    {
        var table = Read("a;b\r1;2\n3;4\r\n5;6");

        Assert.Equal([["1", "2"], ["3", "4"], ["5", "6"]], table.Rows.Select(r => r.ToArray()));
    }

    [Fact]
    public void Read_SkipsBlankLinesAndEmptyRowsAndTrimsHeaders()
    {
        var table = Read("  key ; value \n\n;;\n  ; \nk1;v1\n\n");

        Assert.Equal(["key", "value"], table.Headers);
        Assert.Equal([["k1", "v1"]], table.Rows.Select(r => r.ToArray()));
    }

    [Fact]
    public void Read_ShortRowsReturnEmptyCells()
    {
        var table = Read("a;b;c\n1\n1;2;3;4");

        Assert.Equal(string.Empty, table.Cell(0, 2));
        Assert.Equal(string.Empty, table.Cell(0, -1));
        Assert.Equal(4, table.Rows[1].Count);
    }

    [Fact]
    public void Read_IsLenientLikeExcel()
    {
        var table = Read("a;b\n\"quoted\"tail;un\"quoted\n");

        Assert.Equal("quotedtail", table.Cell(0, 0));
        Assert.Equal("un\"quoted", table.Cell(0, 1));
    }

    [Fact]
    public void Read_UnterminatedQuoteReportsLine()
    {
        var error = Assert.Throws<FormatException>(() => Read("a;b\n1;2\n3;\"never\nends"));

        Assert.Contains("line 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_EmptyDocument()
    {
        var table = Read(string.Empty);

        Assert.Empty(table.Headers);
        Assert.Empty(table.Rows);
    }

    [Fact]
    public void Read_LongInputAcrossBufferBoundaries()
    {
        var value = new string('ż', 10_000);
        var table = Read($"a;b\n\"{value}\";2\n");

        Assert.Equal(value, table.Cell(0, 0));
        Assert.Equal("2", table.Cell(0, 1));
    }

    [Fact]
    public void ReadRecords_StreamsRecordsIncludingHeader()
    {
        using var reader = new StringReader("a;b\n1;2\n3;4\n");

        using var records = _codec.ReadRecords(reader, ';').GetEnumerator();

        Assert.True(records.MoveNext());
        Assert.Equal(["a", "b"], records.Current);
        Assert.True(records.MoveNext());
        Assert.Equal(["1", "2"], records.Current);
        Assert.True(records.MoveNext());
        Assert.False(records.MoveNext());
    }

    [Fact]
    public void Write_QuotesWhenNeededAndUsesCrLf()
    {
        var csv = Write(["Name", "Opis"], [["Kowalski; Jan", "Powiedział \"cześć\""], ["Linia 1\nLinia 2", " spacja "], ["zwykły", ""]]);

        Assert.Equal(
            "Name;Opis\r\n\"Kowalski; Jan\";\"Powiedział \"\"cześć\"\"\"\r\n\"Linia 1\nLinia 2\";\" spacja \"\r\nzwykły;\r\n",
            csv);
    }

    [Theory]
    [InlineData("=SUM(A1:A2)", "'=SUM(A1:A2)")]
    [InlineData("+48 22 123", "'+48 22 123")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tx", "'\tx")]
    [InlineData("a=b", "a=b")]
    public void Write_ProtectsAgainstFormulaInjection(string value, string expected)
    {
        Assert.Equal($"h\r\n{expected}\r\n", Write(["h"], [[value]]));
        Assert.Equal($"h\r\n{value}\r\n", Write(["h"], [[value]], protect: false));
    }

    [Fact]
    public void Write_CarriageReturnPrefixIsProtectedAndQuoted()
    {
        Assert.Equal("h\r\n\"'\rx\"\r\n", Write(["h"], [["\rx"]]));
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        string[][] rows =
        [
            ["jan.kowalski", "Księgowość, Kadry", "\"cytat\""],
            ["żaneta.szymańska", "Linia 1\r\nLinia 2", ""],
            ["x", ";", "tab\there"],
        ];

        foreach (var delimiter in new[] { ';', ',', '\t' })
        {
            var csv = Write(["login", "dział", "uwagi"], rows, delimiter, protect: false);
            var table = Read(csv);

            Assert.Equal(delimiter, table.Delimiter);
            Assert.Equal(rows, table.Rows.Select(r => r.ToArray()));
        }
    }

    [Theory]
    [InlineData('"')]
    [InlineData('\n')]
    [InlineData('\r')]
    public void InvalidDelimiter_Throws(char delimiter)
    {
        Assert.Throws<ArgumentException>(() => Write(["a"], [], delimiter));
        Assert.Throws<ArgumentException>(() => Read("a", delimiter));
    }

    [Fact]
    public void FormatField_IsUsableStandalone()
    {
        Assert.Equal("\"a,b\"", CsvCodec.FormatField("a,b", ','));
        Assert.Equal("a,b", CsvCodec.FormatField("a,b", ';'));
        Assert.Equal(string.Empty, CsvCodec.FormatField(null));
    }
}
