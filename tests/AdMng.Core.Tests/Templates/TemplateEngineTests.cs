using AdMng.Core.Templates;

namespace AdMng.Core.Tests.Templates;

public class TemplateEngineTests
{
    private readonly TemplateEngine _engine = new();

    private static TemplateContext Context(Dictionary<string, string[]>? attributes = null, string? dn = null, int counter = 1) => new()
    {
        Attribute = name => attributes is not null && attributes.TryGetValue(name, out var values) ? values : [],
        DistinguishedName = dn,
        Domain = null,
        Counter = counter,
        Now = new DateTimeOffset(2026, 10, 13, 14, 5, 9, TimeSpan.FromHours(2)),
    };

    private string Render(string template, string givenName = "Łukasz", string sn = "Żółć-Wiśniewski") =>
        _engine.Render(template, Context(new() { ["givenName"] = [givenName], ["sn"] = [sn] }));

    [Theory]
    [InlineData("{givenName|first:1|nodiacritics|lower}{sn|nodiacritics|lower}", "lzolc-wisniewski")]
    [InlineData("{givenName|nodiacritics|lower}.{sn|nodiacritics|lower|replace:-:.}@contoso.com", "lukasz.zolc.wisniewski@contoso.com")]
    [InlineData("{givenName|upper} {sn|lower}", "ŁUKASZ żółć-wiśniewski")]
    [InlineData("{givenName|last:3}", "asz")]
    [InlineData("{givenName|first:100}", "Łukasz")]
    [InlineData("{givenName|first:0}x", "x")]
    [InlineData("{givenName|last:0}x", "x")]
    [InlineData("{missing|last:0}x", "x")]
    [InlineData("{givenName|last:100}", "Łukasz")]
    [InlineData("{sn|alnum}", "ŻółćWiśniewski")]
    [InlineData("{ givenName | lower }", "łukasz")]
    [InlineData("{missing|default:brak danych}", "brak danych")]
    [InlineData("{missing|default:a:b}", "a:b")]
    [InlineData("{givenName|default:x}", "Łukasz")]
    [InlineData("{missing}", "")]
    [InlineData("{{literal}} {givenName} }}", "{literal} Łukasz }")]
    [InlineData("C:\\Users\\{givenName|sam|lower}", "C:\\Users\\lukasz")]
    public void Render_AppliesFunctionsLeftToRight(string template, string expected)
    {
        Assert.Equal(expected, Render(template));
    }

    [Theory]
    [InlineData("jAN kOWALSKI-nOWAK", "Jan Kowalski-Nowak")]
    [InlineData("o'brien", "O'Brien")]
    [InlineData("ŁUKASZ żółć", "Łukasz Żółć")]
    public void Title_CapitalizesWords(string value, string expected)
    {
        Assert.Equal(expected, Render("{givenName|title}", givenName: value));
    }

    [Theory]
    [InlineData("  spaced out  ", "{givenName|trim}", "spaced out")]
    [InlineData(" a b\tc ", "{givenName|nospaces}", "abc")]
    [InlineData("a:b", "{givenName|replace:\\::-}", "a-b")]
    [InlineData("", "{givenName|default:a\\|b}", "a|b")]
    [InlineData("x}y", "{givenName|replace:\\}:-}", "x-y")]
    public void EscapesAndWhitespaceFunctionsWork(string value, string template, string expected)
    {
        Assert.Equal(expected, Render(template, givenName: value));
    }

    [Fact]
    public void RemoveDiacritics_MapsAllPolishLetters()
    {
        Assert.Equal("acelnoszz ACELNOSZZ", _engine.RemoveDiacritics("ąćęłńóśźż ĄĆĘŁŃÓŚŹŻ"));
        Assert.Equal("Lukasz Zolc", _engine.RemoveDiacritics("Łukasz Żółć"));
        Assert.Equal("Zazolc gesla jazn", _engine.RemoveDiacritics("Zażółć gęślą jaźń"));
    }

    [Theory]
    [InlineData("Crème Brûlée", "Creme Brulee")]
    [InlineData("Ångström Čapek Dvořák", "Angstrom Capek Dvorak")]
    [InlineData("Straße Øresund Æsir Œuvre", "Strasse Oresund AEsir OEuvre")]
    [InlineData("Đorđe Þór", "Dorde Thor")]
    [InlineData("plain ascii 123", "plain ascii 123")]
    [InlineData("Ελληνικά", "Ελληνικα")]
    [InlineData("", "")]
    public void RemoveDiacritics_HandlesGeneralLatin(string value, string expected)
    {
        Assert.Equal(expected, _engine.RemoveDiacritics(value));
    }

    [Theory]
    [InlineData("Łukasz Żółć", "LukaszZolc")]
    [InlineData("a\"b/c\\d[e]f:g;h|i=j,k+l*m?n<o>p@q", "abcdefghijklmnopq")]
    [InlineData("Konstantynopolitańczykowianeczka", "Konstantynopolitancz")]
    [InlineData("abcdefghijklmnopqrs.tuvw", "abcdefghijklmnopqrs")]
    [InlineData("jan.kowalski.", "jan.kowalski")]
    [InlineData("名前 x", "x")]
    public void Sam_ProducesValidSamAccountName(string value, string expected)
    {
        var result = Render("{givenName|sam}", givenName: value);

        Assert.Equal(expected, result);
        Assert.True(result.Length <= TemplateEngine.MaxSamAccountNameLength);
    }

    [Fact]
    public void SpecialTokens_UseContext()
    {
        var context = Context(dn: @"CN=Kowalski\, Jan,OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test", counter: 7);

        Assert.Equal(@"CN=Kowalski\, Jan,OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test", _engine.Render("{$dn}", context));
        Assert.Equal("Kowalski, Jan", _engine.Render("{$rdn}", context));
        Assert.Equal("OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test", _engine.Render("{$parent}", context));
        Assert.Equal("ksiegowosc", _engine.Render("{$parentName|nodiacritics|lower}", context));
        Assert.Equal("contoso.test", _engine.Render("{$domain}", context));
        Assert.Equal("example.org", _engine.Render("{$domain}", context with { Domain = "example.org" }));
        Assert.Equal("7 007", _engine.Render("{$counter} {$counter:000}", context));
        Assert.Equal("2026-10-13", _engine.Render("{$date}", context));
        Assert.Equal("20261013 14:05", _engine.Render("{$date:yyyyMMdd HH:mm}", context));
        Assert.Equal("user-007@contoso.test", _engine.Render("user-{$counter:000}@{$DOMAIN}", context));
    }

    [Fact]
    public void SpecialTokens_WithoutDnAreEmpty()
    {
        Assert.Equal("[|||]", _engine.Render("[{$dn}|{$rdn}|{$parent}|{$domain}]", Context()));
    }

    [Fact]
    public void Render_UsesFirstValueOfMultiValuedAttributes()
    {
        var context = Context(new() { ["otherTelephone"] = ["111", "222"] });

        Assert.Equal("111", _engine.Render("{otherTelephone}", context));
    }

    [Fact]
    public void Parse_ReportsReferencedAttributesOnce()
    {
        var result = _engine.Parse("{givenName|lower}.{sn} {GivenName} {$dn} {mail|default:x} {{notAToken}}");

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(["givenName", "sn", "mail"], result.ReferencedAttributes);
    }

    [Theory]
    [InlineData("{givenName", "Unclosed '{' at position 0.")]
    [InlineData("abc}", "Unexpected '}' at position 3; use '}}' for a literal brace.")]
    [InlineData("{}", "Empty token at position 0.")]
    [InlineData("x{givenName|foo}", "Unknown function 'foo' at position 12.")]
    [InlineData("{givenName|first:a}", "Function 'first' at position 11 requires one non-negative integer argument, e.g. 'first:1'.")]
    [InlineData("{givenName|lower:1}", "Function 'lower' at position 11 does not take arguments.")]
    [InlineData("{givenName|replace:a}", "Function 'replace' at position 11 requires two arguments")]
    [InlineData("{givenName|replace::b}", "Function 'replace' at position 11 requires a non-empty text to find.")]
    [InlineData("{givenName|default}", "Function 'default' at position 11 requires an argument")]
    [InlineData("{$nope}", "Unknown special token '$nope' at position 1.")]
    [InlineData("{$dn:x}", "Special token '$dn' at position 1 does not take an argument.")]
    [InlineData("{$counter:Q}", "Invalid format 'Q' for '$counter' at position 1.")]
    [InlineData("{1abc}", "Invalid attribute name '1abc' at position 1.")]
    [InlineData("{given name}", "Invalid attribute name 'given name' at position 1.")]
    [InlineData("{a{b}", "Unexpected '{' at position 2 inside the token starting at position 0")]
    [InlineData("{givenName|}", "Empty function name in the token starting at position 0.")]
    [InlineData("{a\\", "Dangling escape character at position 2.")]
    public void Parse_ReportsErrorsWithPositions(string template, string expectedStart)
    {
        var result = _engine.Parse(template);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.StartsWith(expectedStart, StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_ReportsAllErrors()
    {
        var result = _engine.Parse("{a|nope} } {$x}");

        Assert.Equal(3, result.Errors.Count);
    }

    [Fact]
    public void Render_ThrowsForInvalidTemplates()
    {
        var error = Assert.Throws<FormatException>(() => _engine.Render("{givenName|nope}", Context()));

        Assert.Contains("Unknown function 'nope'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Engine_IsThreadSafe()
    {
        var results = new string[200];

        Parallel.For(0, results.Length, i =>
            results[i] = _engine.Render($"{{givenName|nodiacritics|lower}}-{i}", Context(new() { ["givenName"] = ["Żaneta"] })));

        Assert.All(results.Select((r, i) => (r, i)), x => Assert.Equal($"zaneta-{x.i}", x.r));
    }
}
