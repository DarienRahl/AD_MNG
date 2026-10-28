using System.Text;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Testing;

namespace AdMng.Core.Tests.Correspondence.Composition;

public class OutlookSignatureParserTests
{
    private static readonly Encoding Cp1250 = CodePagesEncodingProvider.Instance.GetEncoding(1250)!;

    /// <summary>A signature as classic Outlook writes it (Word HTML): windows-1250, a style sheet, VML with an image
    /// fallback in conditional comments, <c>o:p</c>, helper links.</summary>
    private const string OutlookSignature = """
        <html xmlns:v="urn:schemas-microsoft-com:vml" xmlns:o="urn:schemas-microsoft-com:office:office" xmlns:w="urn:schemas-microsoft-com:office:word">
        <head>
        <meta http-equiv=Content-Type content="text/html; charset=windows-1250">
        <meta name=ProgId content=Word.Document>
        <link rel=File-List href="Jan%20Kowalski_files/filelist.xml">
        <link rel=Edit-Time-Data href="Jan%20Kowalski_files/editdata.mso">
        <!--[if gte mso 9]><xml><o:OfficeDocumentSettings><o:AllowPNG/></o:OfficeDocumentSettings></xml><![endif]-->
        <style>
        <!--
         /* Font Definitions */
         @font-face {font-family:"Cambria Math";}
        p.MsoNormal, li.MsoNormal, div.MsoNormal {margin:0cm; font-size:11.0pt; font-family:"Calibri",sans-serif;}
        span.Nazwisko {color:#1F3864; font-weight:bold;}
        -->
        </style>
        </head>
        <body lang=PL link="#0563C1" vlink="#954F72" style='word-wrap:break-word'>
        <div class=WordSection1>
        <p class=MsoNormal><span class=Nazwisko>Łukasz Wiśniewski<o:p></o:p></span></p>
        <p class=MsoNormal>Kierownik Działu Księgowości, tel. 42 123 45 67<o:p></o:p></p>
        <p class=MsoNormal><span style='mso-no-proof:yes'><!--[if gte vml 1]><v:shape id="Obraz_x0020_1" style='width:90pt;height:30pt'><v:imagedata src="Jan%20Kowalski_files/image001.png" o:title=""/></v:shape><![endif]--><![if !vml]><img width=120 height=40 src="Jan%20Kowalski_files/image002.png" alt="Logo firmy" v:shapes="Obraz_x0020_1"><![endif]></span><o:p></o:p></p>
        <p class=MsoNormal><a href="https://www.firma.pl">www.firma.pl</a></p>
        </div>
        </body>
        </html>
        """;

    private sealed class Files(Dictionary<string, byte[]?> files)
    {
        public List<string> Requested { get; } = [];

        public byte[]? Read(string path)
        {
            Requested.Add(path);
            return files.GetValueOrDefault(path);
        }
    }

    private static readonly byte[] Logo = MessageImageSamples.Png(12, 4, seed: 0x21, withMetadata: true);

    [Fact]
    public void OutlookSignature_IsInlinedSanitizedAndItsImageEmbedded()
    {
        var files = new Files(new() { ["Jan Kowalski_files/image002.png"] = Logo });
        var result = OutlookSignatureParser.Parse("Jan Kowalski", Cp1250.GetBytes(OutlookSignature), files.Read, 1252);
        var html = result.Signature.BodyHtml;
        Assert.Empty(result.Problems);
        Assert.Equal("Jan Kowalski", result.Signature.Name);
        Assert.Equal(["Jan Kowalski_files/image002.png"], files.Requested);   // the VML image in the comment is never read
        var image = Assert.Single(result.Signature.Images);
        Assert.Contains($"src=\"cid:{image.ContentId}\"", html, StringComparison.Ordinal);
        Assert.Contains("Łukasz Wiśniewski", html, StringComparison.Ordinal);
        Assert.Contains("Działu Księgowości", html, StringComparison.Ordinal);
        Assert.Contains("font-family:", html, StringComparison.Ordinal);
        Assert.Contains("color:#1f3864", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"https://www.firma.pl\"", html, StringComparison.Ordinal);
        foreach (var gone in new[] { "<style", "o:p", "v:shape", "MsoNormal", "<link", "<!--", "mso-", "class=", "<xml", "filelist" })
        {
            Assert.DoesNotContain(gone, html, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(UserMessageRules.FindForbiddenHtml(html));
        var ids = result.Signature.Images.Select(i => i.ContentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(html, MessageHtmlSanitizer.Sanitize(html, new HtmlSanitizeOptions { Mode = HtmlSanitizeMode.Fragment, InlineImageIds = ids }).Html);
        Assert.True(image.Data.Length < Logo.Length);   // metadata stripped
    }

    [Fact]
    public void Utf8WithoutMeta_IsDecodedByContent()
    {
        var result = OutlookSignatureParser.Parse("x", Encoding.UTF8.GetBytes("<p>Żaneta Łącka</p>"), _ => null, 1250);
        Assert.Contains("Żaneta Łącka", result.Signature.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void AnsiWithoutMeta_UsesTheCodePage()
    {
        var result = OutlookSignatureParser.Parse("x", Cp1250.GetBytes("<p>Żaneta Łącka</p>"), _ => null, 1250);
        Assert.Contains("Żaneta Łącka", result.Signature.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableAndInvalidImages_AreDroppedWithProblems()
    {
        var files = new Files(new() { ["a_files/zly.png"] = Encoding.ASCII.GetBytes("to nie jest obraz"), ["a_files/brak.png"] = null });
        var result = OutlookSignatureParser.Parse("a",
            Encoding.UTF8.GetBytes("<p>Podpis <img src=\"a_files/brak.png\" alt=\"x\"> <img src=\"a_files/zly.png\"></p>"), files.Read, 1250);
        Assert.Equal(["signature.imageUnreadable: a_files/brak.png", "signature.imageRejected: UnknownFormat"], result.Problems);
        Assert.Empty(result.Signature.Images);
        Assert.DoesNotContain("<img", result.Signature.BodyHtml, StringComparison.Ordinal);
        Assert.Contains("Podpis", result.Signature.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderExceptions_AreUnreadableImages()
    {
        var result = OutlookSignatureParser.Parse("a", Encoding.UTF8.GetBytes("<img src=\"a_files/x.png\">"), _ => throw new IOException("zablokowany"), 1250);
        Assert.Equal(["signature.imageUnreadable: a_files/x.png"], result.Problems);
    }

    [Theory]
    [InlineData("../tajne.png")]
    [InlineData("a_files/../../tajne.png")]
    [InlineData("a_files\\..\\tajne.png")]
    [InlineData("%2e%2e/tajne.png")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\serwer\\udzial\\x.png")]
    [InlineData("C:\\Windows\\x.png")]
    [InlineData("file:///C:/x.png")]
    [InlineData("a_files/.../x.png")]
    public void PathsLeavingTheFolder_AreNeverRead(string source)
    {
        var files = new Files([]);
        var result = OutlookSignatureParser.Parse("a", Encoding.UTF8.GetBytes($"<p>x<img src=\"{source}\"></p>"), files.Read, 1250);
        Assert.Empty(files.Requested);
        Assert.Single(result.Problems, p => p.StartsWith(SignatureProblemCodes.ImagePathRefused, StringComparison.Ordinal));
        Assert.Empty(result.Signature.Images);
    }

    [Theory]
    [InlineData("a_files/image001.png", "a_files/image001.png")]
    [InlineData("./a_files//image001.png", "a_files/image001.png")]
    [InlineData("a%20b_files/image%28001%29.png", "a b_files/image(001).png")]
    [InlineData("a_files\\image001.png", "a_files/image001.png")]
    [InlineData("a_files/image001.png?v=2", "a_files/image001.png")]
    public void RelativePaths_AreNormalized(string source, string expected) => Assert.Equal(expected, OutlookSignatureParser.RelativePath(source));

    [Fact]
    public void RemoteImagesAreRemoved_DataImagesEmbedded()
    {
        var data = Convert.ToBase64String(MessageImageSamples.Png(3, 3, seed: 0x33));
        var result = OutlookSignatureParser.Parse("a",
            Encoding.UTF8.GetBytes($"<p><img src=\"https://cdn.firma.pl/logo.png\"><img src=\"data:image/png;base64,{data}\"><img src=\"data:image/svg+xml;base64,PHN2Zz4=\"></p>"),
            _ => null, 1250);
        Assert.Contains("signature.remoteImage: cdn.firma.pl", result.Problems);
        Assert.Contains("signature.imageRejected: data", result.Problems);
        Assert.Single(result.Signature.Images);
        Assert.DoesNotContain("cdn.firma.pl", result.Signature.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveContentIsRemovedWithProblems()
    {
        var result = OutlookSignatureParser.Parse("a",
            Encoding.UTF8.GetBytes("<p onclick=\"x()\">Jan<script>alert(1)</script> <a href=\"javascript:alert(2)\">klik</a></p><iframe src=\"https://x.pl\"></iframe>"), _ => null, 1250);
        var html = result.Signature.BodyHtml;
        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("klik", html, StringComparison.Ordinal);
        Assert.Contains("signature.contentRemoved: script", result.Problems);
        Assert.Contains("signature.contentRemoved: onclick", result.Problems);
        Assert.Contains("signature.linkRemoved: javascript", result.Problems);
        Assert.Equal(result.Problems.Count, result.Problems.Distinct().Count());
    }

    [Fact]
    public void SameImageTwice_IsEmbeddedOnce()
    {
        var files = new Files(new() { ["a_files/l.png"] = Logo, ["a_files/kopia.png"] = Logo });
        var result = OutlookSignatureParser.Parse("a",
            Encoding.UTF8.GetBytes("<p><img src=\"a_files/l.png\"><img src=\"a_files/l.png\"><img src=\"a_files/kopia.png\"></p>"), files.Read, 1250);
        Assert.Single(result.Signature.Images);
        Assert.Equal(["a_files/l.png", "a_files/kopia.png"], files.Requested);
    }

    [Fact]
    public void ImageLimitsPerMessage()
    {
        var count = CorrespondenceLimits.MaxImagesPerMessage + 2;
        var files = Enumerable.Range(0, count).ToDictionary(i => $"a_files/{i}.png", i => (byte[]?)MessageImageSamples.Png(2 + (i % 8), 2 + (i / 8), seed: (byte)i));
        var html = "<p>" + string.Concat(Enumerable.Range(0, count).Select(i => $"<img src=\"a_files/{i}.png\">")) + "</p>";
        var result = OutlookSignatureParser.Parse("a", Encoding.UTF8.GetBytes(html), new Files(files).Read, 1250);
        Assert.Equal(CorrespondenceLimits.MaxImagesPerMessage, result.Signature.Images.Count);
        Assert.Equal(2, result.Problems.Count(p => p == "signature.imageRejected: limit"));
    }

    [Fact]
    public void TooDeep_KeepsNothing()
    {
        var html = string.Concat(Enumerable.Repeat("<div>", 3000)) + "x";
        var result = OutlookSignatureParser.Parse("a", Encoding.UTF8.GetBytes(html), _ => null, 1250);
        Assert.Equal([SignatureProblemCodes.TooDeep], result.Problems);
        Assert.Equal(string.Empty, result.Signature.BodyHtml);
    }

    [Fact]
    public void EmptyFile_IsAnEmptySignature()
    {
        var result = OutlookSignatureParser.Parse("pusty", [], _ => null, 1250);
        Assert.Equal(string.Empty, result.Signature.BodyHtml);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void ProblemCodes_FormatAndSplit()
    {
        Assert.Equal("signature.remoteImage: x.pl", SignatureProblemCodes.Format(SignatureProblemCodes.RemoteImage, "x.pl"));
        Assert.Equal(SignatureProblemCodes.TooDeep, SignatureProblemCodes.Format(SignatureProblemCodes.TooDeep, null));
        Assert.Equal((SignatureProblemCodes.ImageUnreadable, "a: b.png"), SignatureProblemCodes.Split("signature.imageUnreadable: a: b.png"));
        Assert.Equal((SignatureProblemCodes.TooDeep, (string?)null), SignatureProblemCodes.Split(SignatureProblemCodes.TooDeep));
        Assert.Equal(7, SignatureProblemCodes.All.Distinct().Count());
    }

    [Fact]
    public void ToString_ShowsOnlyTheName()
    {
        var result = OutlookSignatureParser.Parse("Jan", Encoding.UTF8.GetBytes("<p>jan@firma.pl</p>"), _ => null, 1250);
        Assert.Equal("CorrespondenceSignature Jan", result.Signature.ToString());
        Assert.DoesNotContain("@", result.ToString(), StringComparison.Ordinal);
    }
}
