using AdMng.Core.Correspondence.Fields;

namespace AdMng.Core.Tests.Correspondence.Fields;

public class FieldTemplateTests
{
    private static readonly Dictionary<string, string?> Values = new(StringComparer.Ordinal)
    {
        ["nazwa"] = "Urząd Gminy Łęczyca",
        ["miejscowosc"] = "Łęczyca",
        ["kodpocztowy"] = "99-100",
        ["pusty"] = string.Empty,
        ["spacje"] = "   ",
        ["rd"] = "R&D <dział>",
        ["wiersze"] = "linia 1\r\nlinia 2\nlinia 3\rlinia 4",
        ["sterujace"] = "a\tb\u0007c\u2028d",
        ["cytat"] = "\"x\" 'y'",
    };

    private static string? Lookup(string field) => Values.GetValueOrDefault(FieldKey.Of(field));

    // ---- Parse ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_FieldsAndFallbacks_WithPositions()
    {
        var template = "Dzień dobry {{ Nazwa }}, {{Kod pocztowy|00-000}} {{ Miejscowość | (brak) }}!";
        var tokens = FieldTemplate.Parse(template);
        Assert.Equal(["Nazwa", "Kod pocztowy", "Miejscowość"], tokens.Select(t => t.Field));
        Assert.Equal([null, "00-000", "(brak)"], tokens.Select(t => t.Fallback));
        Assert.All(tokens, t => Assert.StartsWith("{{", template.Substring(t.Start, t.Length), StringComparison.Ordinal));
        Assert.All(tokens, t => Assert.EndsWith("}}", template.Substring(t.Start, t.Length), StringComparison.Ordinal));
        Assert.Equal("{{ Nazwa }}", template.Substring(tokens[0].Start, tokens[0].Length));
    }

    [Theory]
    [InlineData("{{")]
    [InlineData("{{ niezamknięte")]
    [InlineData("{{}}")]
    [InlineData("{{   }}")]
    [InlineData("{{ | tylko fallback }}")]
    [InlineData("{{a\nb}}")]
    [InlineData("{{a}b}}")]
    [InlineData("{ pojedyncze }")]
    [InlineData("}} {{")]
    public void Parse_LiteralText(string template) => Assert.Empty(FieldTemplate.Parse(template));

    [Fact]
    public void Parse_NoNesting_TheInnerTokenWins()
    {
        var tokens = FieldTemplate.Parse("{{a {{b}} c}}");
        Assert.Equal("b", Assert.Single(tokens).Field);
    }

    [Fact]
    public void Parse_TripleBraces_LeaveOneBraceEachSide()
    {
        var token = Assert.Single(FieldTemplate.Parse("{{{x}}}"));
        Assert.Equal((1, 5), (token.Start, token.Length));
    }

    [Fact]
    public void Parse_NameLengthLimit()
    {
        Assert.Single(FieldTemplate.Parse("{{" + new string('a', FieldTemplate.MaxFieldNameLength) + "}}"));
        Assert.Empty(FieldTemplate.Parse("{{" + new string('a', FieldTemplate.MaxFieldNameLength + 1) + "}}"));
        Assert.Single(FieldTemplate.Parse("{{a|" + new string('b', FieldTemplate.MaxFallbackLength) + "}}"));
        Assert.Empty(FieldTemplate.Parse("{{a|" + new string('b', FieldTemplate.MaxFallbackLength + 1) + "}}"));
    }

    [Fact]
    public void Parse_FirstPipeSplits_EmptyFallbackIsEmpty()
    {
        var tokens = FieldTemplate.Parse("{{a|b|c}} {{d|}}");
        Assert.Equal(("a", "b|c"), (tokens[0].Field, tokens[0].Fallback));
        Assert.Equal(("d", ""), (tokens[1].Field, tokens[1].Fallback));
    }

    [Fact]
    public void Parse_DecodesCharacterReferences()
    {
        var tokens = FieldTemplate.Parse("{{R&amp;D}} {{Kod&nbsp;pocztowy|&lt;brak&gt;}}");
        Assert.Equal(["R&D", "Kod pocztowy"], tokens.Select(t => t.Field));
        Assert.Equal("<brak>", tokens[1].Fallback);
    }

    [Fact]
    public void Parse_Null() => Assert.Empty(FieldTemplate.Parse(null));

    [Fact]
    public void Parse_LongRunsOfBracesAreLinear()
    {
        var template = string.Concat(Enumerable.Repeat("{{", 200_000));
        Assert.Empty(FieldTemplate.Parse(template));
    }

    // ---- UsedFields ----------------------------------------------------------------------------------------------------

    [Fact]
    public void UsedFields_DistinctByKey_FirstSpelling() =>
        Assert.Equal(["Kod pocztowy", "zwrot"], FieldTemplate.UsedFields("{{Kod pocztowy}} {{zwrot}} {{kod-POCZTOWY}} {{ Zwrot |x}}"));

    // ---- Merge ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Merge_Text_ReplacesTokensKeepsTheRest()
    {
        var merged = FieldTemplate.Merge("{{nazwa}} ({{Kod pocztowy}} {{Miejscowość}}) {{ & }} {pojedyncze}", Lookup, FieldEncoding.Text);
        Assert.Equal("Urząd Gminy Łęczyca (99-100 Łęczyca)  {pojedyncze}", merged);
    }

    [Fact]
    public void Merge_Html_EncodesValuesAndLineBreaks_NotTheTemplate()
    {
        var merged = FieldTemplate.Merge("<p title=\"{{cytat}}\">{{rd}} – <b>{{wiersze}}</b></p>", Lookup, FieldEncoding.Html);
        Assert.Equal("<p title=\"&quot;x&quot; &#39;y&#39;\">R&amp;D &lt;dział&gt; – <b>linia 1<br>linia 2<br>linia 3<br>linia 4</b></p>", merged);
    }

    [Fact]
    public void Merge_Header_ControlCharactersBecomeSpaces() =>
        Assert.Equal("Temat: a b c d / linia 1  linia 2 linia 3 linia 4", FieldTemplate.Merge("Temat: {{sterujace}} / {{wiersze}}", Lookup, FieldEncoding.Header));

    [Fact]
    public void Merge_ReportsUnknownAndEmpty_OncePerKey()
    {
        var unknown = new List<string>();
        var empty = new List<string>();
        var merged = FieldTemplate.Merge("{{Nieznane}}|{{pusty}}|{{nieznane}}|{{Pusty}}|{{spacje}}|{{nazwa}}", Lookup, FieldEncoding.Text, unknown, empty);
        Assert.Equal("||||   |Urząd Gminy Łęczyca", merged);
        Assert.Equal(["Nieznane"], unknown);
        Assert.Equal(["pusty", "spacje"], empty);
    }

    [Fact]
    public void Merge_Fallback_ForEmptyAndUnknown()
    {
        var unknown = new List<string>();
        var empty = new List<string>();
        var merged = FieldTemplate.Merge("{{pusty|brak danych}};{{spacje|–}};{{Nieznane|?}};{{nazwa|x}};{{pusty|}}", Lookup, FieldEncoding.Text, unknown, empty);
        Assert.Equal("brak danych;–;?;Urząd Gminy Łęczyca;", merged);
        Assert.Equal(["Nieznane"], unknown);   // a typo stays a warning
        Assert.Empty(empty);                   // an empty value with a fallback is handled
    }

    [Fact]
    public void Merge_FallbackIsEncodedLikeValues() =>
        Assert.Equal("&lt;b&gt;brak&lt;/b&gt; &amp;", FieldTemplate.Merge("{{pusty|&lt;b&gt;brak&lt;/b&gt; &amp;}}", Lookup, FieldEncoding.Html));

    [Fact]
    public void Merge_ValuesAreNeverParsedAgain()
    {
        var merged = FieldTemplate.Merge("{{a}} {{b}}", f => f == "a" ? "{{b}}" : "B", FieldEncoding.Text);
        Assert.Equal("{{b}} B", merged);
    }

    [Fact]
    public void Merge_EmptyTemplate() => Assert.Equal(string.Empty, FieldTemplate.Merge(null, Lookup, FieldEncoding.Html));

    [Fact]
    public void Merge_TemplateWithoutTokens_IsReturnedAsIs()
    {
        var template = "Bez pól {{ i }} {pojedyncze";
        Assert.Equal("Bez pól  {pojedyncze", FieldTemplate.Merge(template, _ => string.Empty, FieldEncoding.Text));
        var plain = "zwykły tekst";
        Assert.Same(plain, FieldTemplate.Merge(plain, Lookup, FieldEncoding.Text));
    }

    [Fact]
    public void Merge_RawFields_OnlyInHtml()
    {
        Func<string, bool> raw = f => f == "lista";
        string? Lookup2(string f) => f == "lista" ? "<ul><li>a</li></ul>" : "x<y";
        Assert.Equal("<ul><li>a</li></ul> x&lt;y", FieldTemplate.MergeCore("{{lista}} {{inne}}", Lookup2, FieldEncoding.Html, null, null, raw));
        Assert.Equal("<ul><li>a</li></ul> x<y", FieldTemplate.MergeCore("{{lista}} {{inne}}", Lookup2, FieldEncoding.Text, null, null, raw));
        Assert.Equal("<ul><li>a</li></ul> x<y", FieldTemplate.MergeCore("{{lista}} {{inne}}", Lookup2, FieldEncoding.Header, null, null, raw));
    }

    [Fact]
    public void Merge_LookupGetsTheDecodedName()
    {
        var asked = new List<string>();
        FieldTemplate.Merge("{{R&amp;D}} {{ Kod&nbsp;pocztowy }}", f =>
        {
            asked.Add(f);
            return "v";
        }, FieldEncoding.Html);
        Assert.Equal(["R&D", "Kod pocztowy"], asked);
    }

    [Theory]
    [InlineData(FieldEncoding.Text, "a<b>\n", "a<b>\n")]
    [InlineData(FieldEncoding.Html, "a<b>\n&'\"", "a&lt;b&gt;<br>&amp;&#39;&quot;")]
    [InlineData(FieldEncoding.Header, "a\r\nb\tc", "a  b c")]
    public void Encode(FieldEncoding encoding, string value, string expected) => Assert.Equal(expected, FieldTemplate.Encode(value, encoding));
}
