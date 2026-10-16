using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdMng.Watchdog.Core.Formatting.AdaptiveCards;

/// <summary>
/// Builds the Power Automate Workflows message (section 14.4):
/// <c>{"type":"message","attachments":[{"contentType":"application/vnd.microsoft.card.adaptive","contentUrl":null,"content":card}]}</c>
/// with exactly one attachment, the card <c>"version": "1.4"</c> and <c>"msteams":{"width":"Full"}</c>, serialized
/// compactly with <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> (an HTTP body, never embedded in HTML).
/// </summary>
public static class AdaptiveCardBuilder
{
    public const string AttachmentContentType = "application/vnd.microsoft.card.adaptive";

    public const string SchemaUrl = "http://adaptivecards.io/schemas/adaptive-card.json";

    public const string DefaultVersion = "1.4";

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>The card object.</summary>
    public static JsonObject Card(IEnumerable<AdaptiveElement> body, IEnumerable<AdaptiveAction> actions, string fallbackText, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(fallbackText);
        var card = new JsonObject
        {
            ["$schema"] = SchemaUrl,
            ["type"] = "AdaptiveCard",
            ["version"] = string.IsNullOrWhiteSpace(version) ? DefaultVersion : version.Trim(),
            ["msteams"] = new JsonObject { ["width"] = "Full" },
            ["fallbackText"] = fallbackText,
            ["body"] = new JsonArray([.. body.Select(e => (JsonNode)e.ToJson())]),
        };
        var actionList = actions.Select(a => (JsonNode)a.ToJson()).ToArray();
        if (actionList.Length > 0)
        {
            card["actions"] = new JsonArray(actionList);
        }

        return card;
    }

    /// <summary>The Workflows message envelope with exactly one attachment.</summary>
    public static JsonObject Message(JsonObject card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject
            {
                ["contentType"] = AttachmentContentType,
                ["contentUrl"] = null,
                ["content"] = card,
            }),
        };
    }

    /// <summary>The UTF-8 bytes of a message.</summary>
    public static byte[] Serialize(JsonNode message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Encoding.UTF8.GetBytes(message.ToJsonString(Options));
    }

    /// <summary>The serialized size of one element (budget estimates).</summary>
    public static int Size(AdaptiveElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return Encoding.UTF8.GetByteCount(element.ToJson().ToJsonString(Options));
    }

    /// <summary>The message bytes of a layout as it is now.</summary>
    public static byte[] Build(CardLayout layout, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Serialize(Message(Card(layout.Body(), layout.AllActions(), layout.FallbackText, version)));
    }
}
