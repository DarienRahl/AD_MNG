using System.Text;
using AdMng.Watchdog.Core.Gpo.Parsers;

namespace AdMng.Watchdog.Core.Tests.Gpo;

/// <summary>Decoding of the GPO text files (stage B design 8.4): the accepted encodings and the hostile ones that fail closed.</summary>
public sealed class GpoTextTests
{
    private const string Sample = "[Startup]\r\n0CmdLine=Zażółć.cmd\r\n";

    [Fact]
    public void Utf16LittleEndianWithBom()
    {
        Assert.Equal(Sample, GpoText.DecodeUnicodeText(GpoFixtures.Utf16(Sample)));
    }

    [Fact]
    public void Utf16LittleEndianWithoutBom()
    {
        Assert.Equal(Sample, GpoText.DecodeUnicodeText(Encoding.Unicode.GetBytes(Sample)));
    }

    [Fact]
    public void Utf8WithAndWithoutBom()
    {
        Assert.Equal(Sample, GpoText.DecodeUnicodeText(GpoFixtures.Utf8Bom(Sample)));
        Assert.Equal(Sample, GpoText.DecodeUnicodeText(Encoding.UTF8.GetBytes(Sample)));
    }

    [Fact]
    public void Utf16BigEndianIsRefused()
    {
        byte[] bytes = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(Sample)];

        Assert.Throws<GpoParseException>(() => GpoText.DecodeUnicodeText(bytes));
    }

    [Fact]
    public void Utf32IsRefused()
    {
        byte[] bytes = [0xFF, 0xFE, 0x00, 0x00, .. Encoding.UTF32.GetBytes(Sample)];

        Assert.Throws<GpoParseException>(() => GpoText.DecodeUnicodeText(bytes));
    }

    [Fact]
    public void OddUtf16LengthIsRefused()
    {
        byte[] bytes = [.. GpoFixtures.Utf16(Sample), 0x41];

        Assert.Throws<GpoParseException>(() => GpoText.DecodeUnicodeText(bytes));
    }

    [Fact]
    public void LoneSurrogateIsRefused()
    {
        byte[] bytes = [0xFF, 0xFE, 0x41, 0x00, 0x00, 0xD8, 0x41, 0x00];

        Assert.Throws<GpoParseException>(() => GpoText.DecodeUnicodeText(bytes));
    }

    [Fact]
    public void NulCharactersAreRefused()
    {
        Assert.Throws<GpoParseException>(() => GpoText.DecodeUnicodeText("[General]\0Version=1"u8));
    }

    [Fact]
    public void InvalidUtf8IsRefusedUnlessAnsiIsAllowed()
    {
        // "Version=1 ó" in Windows-1250 (0xF3) – not UTF-8.
        byte[] ansi = [.. "[General]\r\nVersion=1\r\ndisplayName=Zas"u8, 0xF3, (byte)'b'];

        Assert.Throws<GpoParseException>(() => GpoText.DecodeUnicodeText(ansi));
        Assert.Throws<GpoParseException>(() => GpoText.DecodeAnsiOrUtf8(ansi, allowAnsi: false));
        Assert.Contains("Version=1", GpoText.DecodeAnsiOrUtf8(ansi, allowAnsi: true), StringComparison.Ordinal);
    }

    [Fact]
    public void IniReaderSkipsCommentsAndLinesBeforeTheFirstSection()
    {
        var lines = IniReader.Read("Version=9\n; comment\n[General]\n  Version = 3 \nflag\n[Empty]\n").ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal(("General", "Version", "3"), (lines[0].Section, lines[0].Key, lines[0].Value));
        Assert.Null(lines[1].Value);
    }

    [Fact]
    public void CommaListHonoursQuotes()
    {
        Assert.Equal(["Spooler", "4", "D:(A;;GA;;;BA),x"], IniReader.SplitCommaList("\"Spooler\",4,\"D:(A;;GA;;;BA),x\""));
        Assert.Equal(["a\"b"], IniReader.SplitCommaList("\"a\"\"b\""));
    }
}
