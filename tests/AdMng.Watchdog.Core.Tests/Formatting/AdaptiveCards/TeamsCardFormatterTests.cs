using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting.AdaptiveCards;
using AdMng.Watchdog.Core.Model;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Tests.Formatting.AdaptiveCards;

public sealed class TeamsCardFormatterTests
{
    private const string Marker = "ZŁY_MARKER";

    private static readonly JsonSchemaValidator Schema = new(AdaptiveCardSchema.Json);

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        "message", "AdaptiveCard", "Container", "ColumnSet", "Column", "TextBlock", "RichTextBlock", "TextRun", "FactSet",
        "Action.OpenUrl", "Action.ToggleVisibility",
    };

    [Fact]
    public void AlertCard_IsAValidAdaptiveCard14_InOneAttachment()
    {
        var message = TeamsAlertCardFormatter.Alert(FormattingSamples.PrivilegeGranted(), FormattingSamples.Rule(), FormattingSamples.Context());

        var card = AssertValidMessage(message);

        Assert.Equal("1.4", card["version"]!.GetValue<string>());
        Assert.Equal("attention", card["body"]![0]!["style"]!.GetValue<string>());
        Assert.Equal(@"AD Watchdog: Dodano do grupy uprzywilejowanej Jan Kowalski (jan.kowalski) – CONTOSO\adm.nowak", card["fallbackText"]!.GetValue<string>());
        var action = Assert.Single(card["actions"]!.AsArray());
        Assert.Equal("Action.OpenUrl", action!["type"]!.GetValue<string>());
        Assert.Equal("https://admng-wd.contoso.com:8443/events/0192f3a4-5b6c-7d8e-9f00-112233445566", action["url"]!.GetValue<string>());
        Assert.Contains("Kto", AllText(card), StringComparison.Ordinal);
        EmailAlertFormatterTests.AssertNoMissingKeys(Encoding.UTF8.GetString(message.Body));
    }

    [Theory]
    [InlineData(Severity.Critical, "attention")]
    [InlineData(Severity.High, "attention")]
    [InlineData(Severity.Medium, "warning")]
    [InlineData(Severity.Low, "accent")]
    [InlineData(Severity.Info, "default")]
    public void HeaderStyle_FollowsTheSeverity(Severity severity, string style)
    {
        var message = TeamsAlertCardFormatter.Alert(FormattingSamples.PrivilegeGranted() with { Severity = severity }, null, FormattingSamples.Context());

        Assert.Equal(style, AssertValidMessage(message)["body"]![0]!["style"]!.GetValue<string>());
    }

    [Fact]
    public void DirectoryStrings_AppearOnlyInTextRuns_AndAreDefanged()
    {
        var hostile = FormattingSamples.AttributesChanged() with
        {
            Object = FormattingSamples.Jan with { DisplayName = Marker + " [Kliknij](https://evil.example/a)" },
            Changes =
            [
                new AttributeChange { Attribute = "description", OldValues = [Marker + " **pogrubienie**"], NewValues = [Marker + " https://evil.example/b"], OldCount = 1, NewCount = 1 },
            ],
            Attribution = FormattingSamples.ResolvedNowak with { Workstation = Marker + "-WS", Actor = new PrincipalRef { Account = @"CONTOSO\" + Marker } },
        };

        var card = AssertValidMessage(TeamsAlertCardFormatter.Alert(hostile, null, FormattingSamples.Context()));

        Assert.DoesNotContain(Marker, string.Join("\n", MarkdownTexts(card)), StringComparison.Ordinal);
        var runs = string.Join("\n", Nodes(card).Where(n => Type(n) == "TextRun").Select(n => n["text"]!.GetValue<string>()));
        Assert.Contains(Marker + " [Kliknij](hxxps://evil[.]example/a)", runs, StringComparison.Ordinal);
        Assert.Contains("hxxps://evil[.]example/b", runs, StringComparison.Ordinal);
        Assert.DoesNotContain("https://evil", Encoding.UTF8.GetString(TeamsAlertCardFormatter.Alert(hostile, null, FormattingSamples.Context()).Body),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MoreThanSixChanges_AreHiddenBehindAToggle()
    {
        var changes = Enumerable.Range(1, 10)
            .Select(i => new AttributeChange { Attribute = "extensionAttribute" + i, OldValues = ["stara " + i], NewValues = ["nowa " + i], OldCount = 1, NewCount = 1 })
            .ToList();
        var e = FormattingSamples.AttributesChanged() with { Changes = changes };

        var card = AssertValidMessage(TeamsAlertCardFormatter.Alert(e, null, FormattingSamples.Context()));

        var hidden = Nodes(card).Single(n => n["isVisible"] is { } visible && !visible.GetValue<bool>());
        Assert.Equal(4, hidden["items"]!.AsArray().Count);
        var toggle = card["actions"]!.AsArray().Single(a => Type(a!) == "Action.ToggleVisibility")!;
        Assert.Equal("Pokaż wszystkie zmiany", toggle["title"]!.GetValue<string>());
        Assert.Equal(hidden["id"]!.GetValue<string>(), toggle["targetElements"]![0]!.GetValue<string>());
        Assert.Contains("Zmiany (10)", AllText(card), StringComparison.Ordinal);
    }

    [Fact]
    public void BurstCard_With500PolishEvents_FitsInto24KiB_AndSaysHowManyAreShown()
    {
        var events = Enumerable.Range(1, 500).Select(i => FormattingSamples.UserCreated(i)).ToList();

        var message = TeamsAlertCardFormatter.Burst(events, 0, FormattingSamples.Rule("new-accounts", "Nowe konta"), FormattingSamples.Context());

        Assert.True(message.Body.Length <= CardBudget.MaxBytes, message.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var card = AssertValidMessage(message);
        var texts = AllText(card);
        Assert.Contains("500 zdarzeń: Nowe konta", texts, StringComparison.Ordinal);
        Assert.Matches(@"Pokazano \d+ z 500", texts);
        Assert.Contains("Zażółć Gęślą Jaźń 001", texts, StringComparison.Ordinal);
        Assert.Contains("kolejnych", texts, StringComparison.Ordinal);
    }

    [Fact]
    public void AlertCard_WithHugeChanges_IsCutToTheBudget_KeepingTheHeader()
    {
        var changes = Enumerable.Range(1, 200)
            .Select(i => new AttributeChange { Attribute = "extensionAttribute" + i, OldValues = [new string('ą', 190)], NewValues = [new string('ż', 190)], OldCount = 1, NewCount = 1 })
            .ToList();

        var message = TeamsAlertCardFormatter.Alert(FormattingSamples.AttributesChanged() with { Changes = changes }, null, FormattingSamples.Context());

        Assert.True(message.Body.Length <= CardBudget.MaxBytes);
        var card = AssertValidMessage(message);
        Assert.Contains("Zmiana parametrów konta", AllText(card), StringComparison.Ordinal);
        Assert.Contains("Zmiany (200)", AllText(card), StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_IsMeasuredInUtf8Bytes()
    {
        var layout = new CardLayout { FallbackText = "x" };
        var section = new CardSection();
        for (var i = 0; i < 400; i++)
        {
            section.Rows.Add(new CardRow(RichTextBlock.Of(new string('ł', 100))));
        }

        layout.Sections.Add(section);

        var bytes = CardBudget.Fit(layout, maxBytes: 10_000);

        Assert.InRange(bytes.Length, 9_000, 10_000);
        Assert.True(section.Dropped > 0);
        Assert.Equal(Encoding.UTF8.GetByteCount(Encoding.UTF8.GetString(bytes)), bytes.Length);
    }

    [Fact]
    public void FollowUpAndTestCards_AreValid()
    {
        var followUp = AssertValidMessage(TeamsAlertCardFormatter.FollowUp([FormattingSamples.PrivilegeGranted()], FormattingSamples.Rule(), FormattingSamples.Context()));
        var test = AssertValidMessage(TeamsAlertCardFormatter.Test(FormattingSamples.Context("en"), FormattingSamples.Now));

        Assert.Contains("Uzupełnienie: wykonawca ustalony", AllText(followUp), StringComparison.Ordinal);
        Assert.Contains(@"CONTOSO\adm.nowak", AllText(followUp), StringComparison.Ordinal);
        Assert.Contains("AD Watchdog test message", AllText(test), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutPublicBaseUrl_ThereAreNoOpenUrlActions()
    {
        var card = AssertValidMessage(TeamsAlertCardFormatter.Alert(FormattingSamples.PrivilegeGranted(), null, FormattingSamples.Context(publicBaseUrl: null)));

        Assert.Null(card["actions"]);
    }

    [Fact]
    public void LockoutAndPendingCards_AreValidForEveryDetailLevel()
    {
        foreach (var level in Enum.GetValues<DetailLevel>())
        {
            AssertValidMessage(TeamsAlertCardFormatter.Alert(FormattingSamples.LockedOut(), null, FormattingSamples.Context(level: level)));
            AssertValidMessage(TeamsAlertCardFormatter.Alert(FormattingSamples.PrivilegeGranted(EventAttribution.Pending), null, FormattingSamples.Context(level: level),
                followUpExpected: true));
        }
    }

    /// <summary>Parses and validates a Workflows message; returns the card. The <c>msteams</c> host extension is not part of
    /// the vendored schema and is checked separately.</summary>
    private static JsonObject AssertValidMessage(RenderedHttpMessage message)
    {
        Assert.Equal("application/json; charset=utf-8", message.ContentType);
        var root = JsonNode.Parse(message.Body)!.AsObject();
        Assert.Equal("message", root["type"]!.GetValue<string>());
        var attachment = Assert.Single(root["attachments"]!.AsArray())!.AsObject();
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment["contentType"]!.GetValue<string>());
        Assert.True(attachment.ContainsKey("contentUrl"));
        Assert.Null(attachment["contentUrl"]);
        var card = attachment["content"]!.AsObject();
        Assert.Equal("Full", card["msteams"]!["width"]!.GetValue<string>());
        Assert.All(Nodes(card), n => Assert.Contains(Type(n) ?? "Column", AllowedTypes));
        Assert.DoesNotContain(Nodes(card), n => n["style"]?.GetValue<string>() == "heading");

        var withoutHostExtension = JsonNode.Parse(card.ToJsonString())!.AsObject();
        withoutHostExtension.Remove("msteams");
        using var document = JsonDocument.Parse(withoutHostExtension.ToJsonString());
        var errors = Schema.Validate(document.RootElement);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(10)));
        return card;
    }

    private static IEnumerable<JsonObject> Nodes(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.ContainsKey("type") || obj.ContainsKey("items"))
                {
                    yield return obj;
                }

                foreach (var (_, child) in obj)
                {
                    if (child is not null)
                    {
                        foreach (var nested in Nodes(child))
                        {
                            yield return nested;
                        }
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array.OfType<JsonNode>())
                {
                    foreach (var nested in Nodes(child))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static string? Type(JsonNode node) => node["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : null;

    /// <summary>Texts rendered with Markdown: TextBlock texts and Fact titles/values.</summary>
    private static IEnumerable<string> MarkdownTexts(JsonObject card) =>
        Nodes(card).Where(n => Type(n) == "TextBlock").Select(n => n["text"]!.GetValue<string>())
            .Concat(Nodes(card).Where(n => Type(n) == "FactSet").SelectMany(n => n["facts"]!.AsArray())
                .SelectMany(f => new[] { f!["title"]!.GetValue<string>(), f["value"]!.GetValue<string>() }));

    private static string AllText(JsonObject card) => string.Join("\n",
        Nodes(card).Where(n => Type(n) is "TextBlock" or "TextRun").Select(n => n["text"]!.GetValue<string>()).Concat(MarkdownTexts(card)));
}
