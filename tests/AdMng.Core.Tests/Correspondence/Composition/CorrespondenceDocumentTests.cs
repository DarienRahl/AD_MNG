using AdMng.Core.Correspondence.Composition;

namespace AdMng.Core.Tests.Correspondence.Composition;

public class CorrespondenceDocumentTests
{
    private const string Wrapper = "<div style=\"font-family:Calibri,'Segoe UI',Arial,sans-serif;font-size:11pt;color:#000000;line-height:1.4\">";

    [Fact]
    public void Build_WithoutSignature() =>
        Assert.Equal("<html><head><meta charset=\"utf-8\"></head><body>" + Wrapper + "<p>Treść</p></div></body></html>",
            CorrespondenceDocument.Build("<p>Treść</p>", null));

    [Fact]
    public void Build_SignatureAfterALineBreak() =>
        Assert.Equal("<html><head><meta charset=\"utf-8\"></head><body>" + Wrapper + "<p>Treść</p><br><p>Jan Kowalski</p></div></body></html>",
            CorrespondenceDocument.Build("<p>Treść</p>", new CorrespondenceSignature("Jan", "<p>Jan Kowalski</p>", [])));

    [Fact]
    public void Build_BlankSignatureIsLeftOut() =>
        Assert.DoesNotContain("<br>", CorrespondenceDocument.Build("<p>x</p>", new CorrespondenceSignature("Pusty", "  ", [])), StringComparison.Ordinal);

    [Fact]
    public void Build_HasNoStyleBlock() =>
        Assert.DoesNotContain("<style", CorrespondenceDocument.Build(string.Empty, null), StringComparison.Ordinal);

    [Fact]
    public void WrapperStyle_UsesTheBodyFont() =>
        Assert.Equal("font-family:" + CorrespondenceDocument.BodyFont + ";font-size:11pt;color:#000000;line-height:1.4", CorrespondenceDocument.WrapperStyle);

    [Fact]
    public void BaseStyle_Default_IsTheStyleMessagesHadBefore()
    {
        Assert.Equal(CorrespondenceDocument.WrapperStyle, MessageBaseStyle.Default.Css);
        Assert.Equal(CorrespondenceDocument.BodyFont, MessageBaseStyle.Default.FontFamily);
        Assert.Equal(CorrespondenceDocument.BodyPoints, MessageBaseStyle.Default.FontSizePoints);
        Assert.Equal(CorrespondenceDocument.Build("<p>x</p>", null), CorrespondenceDocument.Build("<p>x</p>", null, MessageBaseStyle.Default));
    }

    [Fact]
    public void Build_WithABaseStyle_WrapsTheBodyInIt() =>
        Assert.Equal(
            "<html><head><meta charset=\"utf-8\"></head><body><div style=\"font-family:Aptos,sans-serif;font-size:12pt;color:#000000;line-height:normal\">"
            + "<p>Treść</p></div></body></html>",
            CorrespondenceDocument.Build("<p>Treść</p>", null, new MessageBaseStyle("Aptos,sans-serif", 12, "normal")));

    [Theory]
    [InlineData("Aptos, sans-serif", 10.5, "1.15", "font-family:Aptos,sans-serif;font-size:10.5pt;color:#000000;line-height:1.15")]
    [InlineData("\"Times New Roman\",serif", 12, "NORMAL", "font-family:'Times New Roman',serif;font-size:12pt;color:#000000;line-height:normal")]
    [InlineData("Arial\" onmouseover=\"x", 12, "normal", "font-family:Calibri,'Segoe UI',Arial,sans-serif;font-size:12pt;color:#000000;line-height:normal")]
    [InlineData("Arial;position:fixed", 12, "normal", "font-family:Calibri,'Segoe UI',Arial,sans-serif;font-size:12pt;color:#000000;line-height:normal")]
    [InlineData("Aptos,sans-serif", 0, "normal", "font-family:Aptos,sans-serif;font-size:11pt;color:#000000;line-height:normal")]
    [InlineData("Aptos,sans-serif", 500, "normal", "font-family:Aptos,sans-serif;font-size:11pt;color:#000000;line-height:normal")]
    [InlineData("Aptos,sans-serif", double.NaN, "normal", "font-family:Aptos,sans-serif;font-size:11pt;color:#000000;line-height:normal")]
    [InlineData("Aptos,sans-serif", 12, "1.4;color:red", "font-family:Aptos,sans-serif;font-size:12pt;color:#000000;line-height:1.4")]
    [InlineData("Aptos,sans-serif", 12, "-2", "font-family:Aptos,sans-serif;font-size:12pt;color:#000000;line-height:1.4")]
    public void BaseStyle_Css_KeepsOnlyValidValues(string family, double size, string lineHeight, string expected) =>
        Assert.Equal(expected, new MessageBaseStyle(family, size, lineHeight).Css);

    [Fact]
    public void BaseStyle_Of_StoredValues_MissingOrInvalidPartsAreTheDefaults()
    {
        Assert.Equal(MessageBaseStyle.Default, MessageBaseStyle.Of(null, null, null));
        Assert.Equal(new MessageBaseStyle("Aptos,sans-serif", 12, "normal"), MessageBaseStyle.Of(" Aptos , sans-serif ", 12, " normal "));
        Assert.Equal(new MessageBaseStyle(CorrespondenceDocument.BodyFont, 10.5, "1.4"), MessageBaseStyle.Of("<script>", 10.54, "wysoka"));
        Assert.Null(MessageBaseStyle.NormalizeFontSize(MessageBaseStyle.MinFontSizePoints - 0.5));
        Assert.Equal(MessageBaseStyle.MaxFontSizePoints, MessageBaseStyle.NormalizeFontSize(MessageBaseStyle.MaxFontSizePoints));
        Assert.Null(MessageBaseStyle.NormalizeFontFamily("  "));
        Assert.Null(MessageBaseStyle.NormalizeLineHeight(null));
    }

    [Fact]
    public void Renderer_WrapsTheMessageInTheDraftsBaseStyle()
    {
        var unit = new CorrespondenceUnit("anna.nowak@contoso.test", "Anna Nowak", [new Dictionary<string, string>()]);
        var plain = new CorrespondenceDraft { Subject = "Temat", BodyHtml = "<p>Dzień dobry</p>" };

        var aptos = CorrespondenceRenderer.Render(plain with { BaseStyle = new MessageBaseStyle("Aptos,sans-serif", 12, "normal") }, unit, []);
        var standard = CorrespondenceRenderer.Render(plain, unit, []);

        Assert.Equal(new Dictionary<string, string>
        {
            ["font-family"] = "Aptos,sans-serif",
            ["font-size"] = "12pt",
            ["color"] = "#000000",
            ["line-height"] = "normal",
        }, WrapperDeclarations(aptos.HtmlDocument));
        Assert.Equal("Calibri,'Segoe UI',Arial,sans-serif", WrapperDeclarations(standard.HtmlDocument)["font-family"]);
        Assert.Equal("1.4", WrapperDeclarations(standard.HtmlDocument)["line-height"]);
    }

    /// <summary>The declarations of the <c>div</c> that wraps a rendered document (the sanitizer may reorder them).</summary>
    private static Dictionary<string, string> WrapperDeclarations(string document)
    {
        var parsed = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(document);
        var wrapper = Assert.Single(parsed.Body!.Children);
        Assert.Equal("div", wrapper.LocalName);
        return wrapper.GetAttribute("style")!
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Split(':', 2))
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("<html><body><p>a</p></body></html>", "<p>a</p>")]
    [InlineData("<!DOCTYPE html>\n<html>\n<head></head>\n<BODY class=\"x\">\n<div>b</div>\n</BODY>\n</html>\n", "<div>b</div>")]
    [InlineData("<p>bez body</p>", "<p>bez body</p>")]
    [InlineData("<bodyguard>x</bodyguard><body>c", "c")]
    [InlineData("<body>tylko otwarte", "tylko otwarte")]
    [InlineData("<body/>", "")]
    public void BodyOf_TheInnerContent(string document, string expected) => Assert.Equal(expected, CorrespondenceDocument.BodyOf(document));

    [Fact]
    public void BodyOf_RoundTripWithBuild() =>
        Assert.Equal(Wrapper + "<p>x</p></div>", CorrespondenceDocument.BodyOf(CorrespondenceDocument.Build("<p>x</p>", null)));
}
