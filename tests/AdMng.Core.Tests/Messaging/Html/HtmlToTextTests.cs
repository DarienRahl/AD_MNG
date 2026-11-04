using AdMng.Core.Messaging.Html;

namespace AdMng.Core.Tests.Messaging.Html;

/// <summary>The text part generated from HTML.</summary>
public class HtmlToTextTests
{
    [Theory]
    [InlineData("<p>Dzień dobry,</p><p>hasło wygasa.</p>", "Dzień dobry,\n\nhasło wygasa.")]
    [InlineData("<div>a</div><div>b</div>", "a\nb")]
    [InlineData("<div>a</div><div><br></div><div>b</div>", "a\n\nb")]
    [InlineData("<p>a<br>b</p>", "a\nb")]
    [InlineData("<p>a   b\n  c</p>", "a b c")]
    [InlineData("<p>a&nbsp;&nbsp;b</p>", "a  b")]
    [InlineData("<ul><li>jeden</li><li>dwa</li></ul>", "- jeden\n- dwa")]
    [InlineData("<ol start=\"3\"><li>trzy</li><li>cztery</li></ol>", "3. trzy\n4. cztery")]
    [InlineData("<ul><li>a<ul><li>a1</li></ul></li><li>b</li></ul>", "- a\n  - a1\n- b")]
    [InlineData("<ul><li><div>w divie</div></li></ul>", "- w divie")]
    [InlineData("<table><tr><td>Dział</td><td>Telefon</td></tr><tr><td><p>IT</p></td><td>123</td></tr></table>", "Dział\tTelefon\nIT\t123")]
    [InlineData("<p><a href=\"https://haslo.contoso.pl/\">zmień hasło</a></p>", "zmień hasło (https://haslo.contoso.pl/)")]
    [InlineData("<p><a href=\"https://haslo.contoso.pl/\">haslo.contoso.pl</a></p>", "haslo.contoso.pl")]
    [InlineData("<p><a href=\"mailto:pomoc@contoso.pl\">pomoc@contoso.pl</a></p>", "pomoc@contoso.pl")]
    [InlineData("<p><a href=\"{$input:link}\">formularz</a></p>", "formularz ({$input:link})")]
    [InlineData("<p><img src=\"cid:x\" alt=\"Logo\"> <img src=\"cid:y\"></p>", "[Logo]")]
    [InlineData("<pre>  kod\n    wcięty</pre>", "  kod\n    wcięty")]
    [InlineData("<h1>Tytuł</h1><p>treść</p><hr><p>stopka</p>", "Tytuł\n\ntreść\n\n----------\n\nstopka")]
    [InlineData("<p>{givenName}, {$passwordExpires:d MMMM yyyy}</p>", "{givenName}, {$passwordExpires:d MMMM yyyy}")]
    [InlineData("<style>p{color:red}</style><p>x</p>", "x")]
    [InlineData("", "")]
    public void Html_BecomesText(string html, string expected) => Assert.Equal(expected, HtmlToText.Convert(html));

    [Fact]
    public void HtmlNestedTooDeeply_GivesNoText_WithoutAWalkOverIt() =>
        Assert.Equal(string.Empty, SmallStack.Run(() => HtmlToText.Convert(SmallStack.Nested(5_000))));

    [Fact]
    public void Documents_UseTheirBody() =>
        Assert.Equal("treść", HtmlToText.Convert("<!DOCTYPE html><html><head><title>t</title></head><body><p>treść</p></body></html>"));
}
