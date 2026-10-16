using System.Text.Json;

namespace AdMng.Watchdog.Core.Tests.Formatting.AdaptiveCards;

/// <summary>The schema check of the card tests really rejects invalid cards.</summary>
public sealed class JsonSchemaValidatorTests
{
    private static readonly JsonSchemaValidator Schema = new(AdaptiveCardSchema.Json);

    [Fact]
    public void AMinimalCard_IsValid() =>
        Assert.Empty(Validate("""{"type":"AdaptiveCard","version":"1.4","body":[{"type":"TextBlock","text":"Cześć"}]}"""));

    [Theory]
    [InlineData("""{"type":"AdaptiveCard","version":"1.4","body":[{"type":"TextBlock"}]}""")]
    [InlineData("""{"type":"AdaptiveCard","version":"1.4","body":[{"type":"TextBlock","text":"x","colour":"red"}]}""")]
    [InlineData("""{"type":"AdaptiveCard","version":"1.4","msteams":{"width":"Full"},"body":[]}""")]
    [InlineData("""{"type":"AdaptiveCard","version":"1.4","body":[{"type":"Table","rows":[]}]}""")]
    [InlineData("""{"type":"AdaptiveCard","version":"1.4","body":[{"type":"Container","style":"purple","items":[]}]}""")]
    [InlineData("""{"type":"AdaptiveCard","version":"1.4","body":[{"type":"RichTextBlock","inlines":[{"type":"TextRun","text":"x","bold":true}]}]}""")]
    [InlineData("""{"type":"AdaptiveCard","version":"1.4","body":[],"actions":[{"type":"Action.OpenUrl","title":"x"}]}""")]
    public void InvalidCards_AreRejected(string json) => Assert.NotEmpty(Validate(json));

    private static IReadOnlyList<string> Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Schema.Validate(document.RootElement);
    }
}
