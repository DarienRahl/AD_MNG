using System.Net;
using AdMng.Core.Templates;

namespace AdMng.Core.Tests.Templates;

/// <summary>Extension tokens, the value encoder and the empty-value callback of <see cref="TemplateEngine"/> (U1).</summary>
public class TemplateExtensionTests
{
    private static readonly TemplateEngine Engine = new(
    [
        new TemplateExtensionToken("expires", TemplateTokenArgument.Optional, IsDateFormat: true),
        new TemplateExtensionToken("days", TemplateTokenArgument.None),
        new TemplateExtensionToken("input", TemplateTokenArgument.Required),
    ]);

    private static TemplateContext Context(Func<string, string>? encode = null, Action<string>? empty = null) => new()
    {
        Attribute = a => a == "givenName" ? ["Łukasz"] : a == "sn" ? ["Wiśniewski <b>"] : [],
        Extension = (name, argument) => name switch
        {
            "expires" => "5 listopada 2026" + (argument is null ? string.Empty : "|" + argument),
            "days" => "7",
            "input" => argument == "termin" ? "30 listopada" : string.Empty,
            _ => "?",
        },
        Encode = encode,
        EmptyValue = empty,
    };

    [Fact]
    public void ExtensionTokens_AreEvaluatedByTheContext_WithTheirArgumentAndFunctions()
    {
        var text = Engine.Render("{givenName}: {$expires} / {$expires:d MMMM yyyy, HH:mm} / {$days} / {$input:termin|upper}", Context());

        Assert.Equal("Łukasz: 5 listopada 2026 / 5 listopada 2026|d MMMM yyyy, HH:mm / 7 / 30 LISTOPADA", text);
    }

    [Fact]
    public void Encoder_AppliesToValuesOnly_NeverToTheLiteralText()
    {
        var html = Engine.Render("<p>{givenName} {sn}</p>", Context(WebUtility.HtmlEncode));

        Assert.Equal("<p>Łukasz Wiśniewski &lt;b&gt;</p>", html);
    }

    [Fact]
    public void EmptyValues_AreReportedWithTheTokenSource()
    {
        var empty = new List<string>();

        Engine.Render("{givenName} {department} {$input:inne} {department|upper}", Context(empty: empty.Add));

        Assert.Equal(["{department}", "{$input:inne}", "{department|upper}"], empty);
    }

    [Fact]
    public void Parse_ListsTheSpecialTokens_AndValidatesArguments()
    {
        var parsed = Engine.Parse("{$expires:dd.MM.yyyy} {$input:termin} {$input:termin} {$domain}");

        Assert.True(parsed.IsValid);
        Assert.Equal(
            [new TemplateTokenUse("expires", "dd.MM.yyyy"), new TemplateTokenUse("input", "termin"), new TemplateTokenUse("domain", null)],
            parsed.SpecialTokens);
        Assert.Contains(Engine.Parse("{$days:x}").Errors, e => e.Contains("does not take an argument", StringComparison.Ordinal));
        Assert.Contains(Engine.Parse("{$input}").Errors, e => e.Contains("requires an argument", StringComparison.Ordinal));
        Assert.Contains(Engine.Parse("{$expires:}").Errors, e => e.Contains("Empty date format", StringComparison.Ordinal));
        Assert.Contains(Engine.Parse("{$unknown}").Errors, e => e.Contains("$expires", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBatchEditorEngine_DoesNotKnowTheExtensions()
    {
        var batch = new TemplateEngine();

        Assert.False(batch.Parse("{$expires}").IsValid);
        Assert.Throws<ArgumentException>(() => new TemplateEngine([new TemplateExtensionToken("date", TemplateTokenArgument.None)]));
    }
}
