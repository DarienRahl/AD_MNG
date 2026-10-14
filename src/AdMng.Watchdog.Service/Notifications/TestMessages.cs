using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// The payloads of the test buttons (spec 15.8): e-mail "[AD Watchdog] Wiadomość testowa", a minimal Adaptive Card 1.4
/// envelope ("Wiadomość testowa AD Watchdog", exactly one attachment) and a <c>watchdog.test</c> webhook event. Texts come
/// from the service resources in the configured notification language; the instance name is the only variable part
/// (HTML-encoded in e-mail, a plain TextRun in the card, JSON-escaped in the webhook).
/// </summary>
public static class TestMessages
{
    public const string WebhookEventType = "watchdog.test";

    public static RenderedEmail Email(WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var culture = ServiceText.Culture(configuration.General.Language);
        var instance = configuration.General.InstanceName;
        var body = ServiceText.Format("Test.Body", culture, instance);
        var footer = ServiceText.Get("Test.Footer", culture);
        var html = new StringBuilder()
            .Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body style=\"font-family:Segoe UI,Arial,sans-serif;max-width:720px\">")
            .Append("<p>").Append(HtmlEncoder.Default.Encode(body)).Append("</p>")
            .Append("<p style=\"color:#616161;font-size:12px\">").Append(HtmlEncoder.Default.Encode(footer)).Append("</p>")
            .Append("</body></html>")
            .ToString();
        return new RenderedEmail
        {
            Subject = ServiceText.Format("Test.EmailSubject", culture, configuration.Notifications.SubjectPrefix),
            HtmlBody = html,
            TextBody = body + "\r\n\r\n" + footer,
        };
    }

    /// <summary>The Teams POST body: <c>{"type":"message","attachments":[{…adaptive card 1.4…}]}</c>.</summary>
    public static byte[] TeamsCard(WatchdogConfiguration configuration, string cardVersion = "1.4")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var culture = ServiceText.Culture(configuration.General.Language);
        var title = ServiceText.Get("Test.Title", culture);
        var body = ServiceText.Format("Test.Body", culture, configuration.General.InstanceName);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("type", "message");
            json.WriteStartArray("attachments");
            json.WriteStartObject();
            json.WriteString("contentType", "application/vnd.microsoft.card.adaptive");
            json.WriteNull("contentUrl");
            json.WriteStartObject("content");
            json.WriteString("$schema", "http://adaptivecards.io/schemas/adaptive-card.json");
            json.WriteString("type", "AdaptiveCard");
            json.WriteString("version", cardVersion);
            json.WriteStartObject("msteams");
            json.WriteString("width", "Full");
            json.WriteEndObject();
            json.WriteString("fallbackText", title);
            json.WriteStartArray("body");
            json.WriteStartObject();
            json.WriteString("type", "TextBlock");
            json.WriteString("text", title);
            json.WriteString("size", "Large");
            json.WriteString("weight", "Bolder");
            json.WriteBoolean("wrap", true);
            json.WriteEndObject();
            json.WriteStartObject();
            json.WriteString("type", "RichTextBlock");
            json.WriteStartArray("inlines");
            json.WriteStartObject();
            json.WriteString("type", "TextRun");
            json.WriteString("text", body);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>The <c>watchdog.test</c> webhook body (compact UTF-8; the signature covers these bytes).</summary>
    public static byte[] Webhook(WatchdogConfiguration configuration, string messageId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("type", WebhookEventType);
            json.WriteString("timestamp", now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
            json.WriteStartObject("data");
            json.WriteNumber("schemaVersion", 1);
            json.WriteString("messageId", messageId);
            json.WriteString("instance", configuration.General.InstanceName);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
