using AdMng.Watchdog.Core.Formatting;

namespace AdMng.Watchdog.Core.Tests.Formatting;

public sealed class HtmlWriterTests
{
    [Fact]
    public void TextAndAttributes_AreEncoded()
    {
        var html = new HtmlWriter()
            .Open("td", ("style", "color:red\" onmouseover=\"x"))
            .Text("<script>alert('x')</script> & Zażółć")
            .Close()
            .ToString();

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\" onmouseover", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Zażółć", html, StringComparison.Ordinal);
        Assert.Contains("&amp;", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("script")]
    [InlineData("img")]
    [InlineData("iframe")]
    public void DangerousElements_AreRejected(string element) =>
        Assert.Throws<ArgumentException>(() => new HtmlWriter().Open(element));

    [Fact]
    public void EventHandlerAttributes_AreRejected() =>
        Assert.Throws<ArgumentException>(() => new HtmlWriter().Open("div", ("onclick", "x()")));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("/relative")]
    public void Links_AcceptOnlyAbsoluteHttpUrls(string url)
    {
        Assert.Throws<ArgumentException>(() => new HtmlWriter().Link(url, "x"));
        Assert.Throws<ArgumentException>(() => new HtmlWriter().Open("a", ("href", url)));
    }

    [Fact]
    public void MultiLineText_BecomesLineBreaks()
    {
        var html = new HtmlWriter().Open("p").MultiLineText("pierwsza\r\n<druga>").Close().ToString();

        Assert.Equal("<p>pierwsza<br>&lt;druga&gt;</p>", html);
    }

    [Fact]
    public void UnclosedElements_AreDetected()
    {
        Assert.Throws<InvalidOperationException>(() => new HtmlWriter().Open("div").ToString());
        Assert.Throws<InvalidOperationException>(() => new HtmlWriter().Close());
        Assert.Equal("<br><hr>", new HtmlWriter().Open("br").Open("hr").ToString());
    }
}
