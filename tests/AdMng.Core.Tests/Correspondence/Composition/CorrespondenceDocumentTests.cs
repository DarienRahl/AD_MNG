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
