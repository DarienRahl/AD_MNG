using System.Text;
using AdMng.Core.Correspondence.Import;

namespace AdMng.Core.Tests.Correspondence.Import;

public class TextDecodingTests
{
    private const string Polish = "Zażółć gęślą jaźń – ŁÓDŹ";

    [Fact]
    public void Decode_Utf8Bom() =>
        Assert.Equal(Polish, TextDecoding.Decode([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Polish)], 1250));

    [Fact]
    public void Decode_Utf16LittleEndianBom() =>
        Assert.Equal(Polish, TextDecoding.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Polish)], 1250));

    [Fact]
    public void Decode_Utf16BigEndianBom() =>
        Assert.Equal(Polish, TextDecoding.Decode([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(Polish)], 1250));

    [Fact]
    public void Decode_Utf32LittleEndianBom() =>
        Assert.Equal(Polish, TextDecoding.Decode([0xFF, 0xFE, 0x00, 0x00, .. Encoding.UTF32.GetBytes(Polish)], 1250));

    [Fact]
    public void Decode_StrictUtf8WithoutBom() =>
        Assert.Equal(Polish, TextDecoding.Decode(Encoding.UTF8.GetBytes(Polish), 1252));

    [Fact]
    public void Decode_Cp1250WhenNotUtf8() =>
        Assert.Equal(Polish, TextDecoding.Decode(ImportTestFiles.Cp1250.GetBytes(Polish), 1250));

    [Fact]
    public void Decode_TheGivenAnsiCodePage()
    {
        var bytes = CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetBytes("Café Müller");
        Assert.Equal("Café Müller", TextDecoding.Decode(bytes, 1252));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99999)]
    [InlineData(65001)]
    public void Decode_UnusableCodePage_FallsBackToWindows1250(int codePage) =>
        Assert.Equal(Polish, TextDecoding.Decode(ImportTestFiles.Cp1250.GetBytes(Polish), codePage));

    [Fact]
    public void Decode_Empty() => Assert.Equal(string.Empty, TextDecoding.Decode([], 1250));

    [Fact]
    public void DecodeHtml_MetaCharsetWins()
    {
        var html = "<html><head><meta http-equiv=Content-Type content=\"text/html; charset=windows-1250\"></head><body>" + Polish + "</body></html>";
        var decoded = TextDecoding.DecodeHtml(ImportTestFiles.Cp1250.GetBytes(html), 1252);
        Assert.Contains(Polish, decoded, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeHtml_Html5Charset()
    {
        var html = "<meta charset='utf-8'><p>" + Polish;
        Assert.Contains(Polish, TextDecoding.DecodeHtml(Encoding.UTF8.GetBytes(html), 1250), StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeHtml_BomBeatsMeta()
    {
        var html = "<meta charset=windows-1250><p>" + Polish;
        Assert.Contains(Polish, TextDecoding.DecodeHtml([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(html)], 1250), StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeHtml_UnknownCharset_DecodesByContent()
    {
        var html = "<meta charset=nie-ma-takiego><p>" + Polish;
        Assert.Contains(Polish, TextDecoding.DecodeHtml(ImportTestFiles.Cp1250.GetBytes(html), 1250), StringComparison.Ordinal);
        Assert.Contains(Polish, TextDecoding.DecodeHtml(Encoding.UTF8.GetBytes(html), 1250), StringComparison.Ordinal);
    }
}
