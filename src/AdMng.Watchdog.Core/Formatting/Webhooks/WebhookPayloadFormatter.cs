using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Formatting.Webhooks;

/// <summary>
/// Generic webhook payloads (section 14.5): compact UTF-8 JSON <c>{"type":…,"timestamp":…,"data":{…}}</c> – the signed
/// bytes are the sent bytes (<see cref="RenderedHttpMessage.Body"/>; the transport adds the Standard Webhooks headers
/// per attempt). One payload per event; detail levels: Minimal keeps attribute names only and the actor's
/// status/account/SID (no workstation/IP), Standard hides personal values, Full shows every non-secret value; secrets are
/// always <c>{"attribute":…,"redacted":true}</c>. Strings pass through <see cref="UntrustedText"/> first and are
/// JSON-escaped by the writer.
/// </summary>
public static class WebhookPayloadFormatter
{
    /// <summary><c>data.schemaVersion</c>.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    /// <summary>The payload of one event (<paramref name="followUp"/>: <c>ad.attribution.updated</c> with the final
    /// attribution).</summary>
    public static RenderedHttpMessage Event(WatchEvent watchEvent, IReadOnlyList<RuleSettings> rules, NotificationContext context, bool followUp = false)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(context);
        var e = DetailLevelFilter.ForNotification(watchEvent, context.DetailLevel, context.MaxValueLength);
        var type = followUp ? WebhookEventTypes.AttributionUpdated : WebhookEventTypes.For(e.Kind, e.Object.Kind);
        var body = Write(type, e.DetectedAt, writer => WriteEventData(writer, e, rules, context));
        return new RenderedHttpMessage { Body = body, WebhookEventType = type, Title = UntrustedText.ForHeader(context.Text.KindTitle(e.Kind)) };
    }

    /// <summary>The <c>watchdog.digest</c> payload: period, counts and (optionally) the event ids of the period.</summary>
    public static RenderedHttpMessage Digest(DateTimeOffset periodStart, DateTimeOffset periodEnd, string? domain,
        IReadOnlyDictionary<string, int> counts, IReadOnlyList<Guid> eventIds, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(eventIds);
        ArgumentNullException.ThrowIfNull(context);
        var body = Write(WebhookEventTypes.Digest, periodEnd, writer =>
        {
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("instance", context.InstanceName);
            WriteNullableString(writer, "domain", domain is null ? null : UntrustedText.Sanitize(domain));
            writer.WriteString("periodStart", Utc(periodStart));
            writer.WriteString("periodEnd", Utc(periodEnd));
            writer.WriteStartObject("counts");
            foreach (var (name, value) in counts.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                writer.WriteNumber(name, value);
            }

            writer.WriteEndObject();
            writer.WriteStartArray("eventIds");
            foreach (var id in eventIds)
            {
                writer.WriteStringValue(id.ToString("D", CultureInfo.InvariantCulture));
            }

            writer.WriteEndArray();
            WriteLinks(writer, context.ServiceLink);
        });
        return new RenderedHttpMessage { Body = body, WebhookEventType = WebhookEventTypes.Digest, Title = context.Text.Get("Digest.Title") };
    }

    /// <summary>The <c>watchdog.heartbeat</c> payload (no directory values).</summary>
    public static RenderedHttpMessage Heartbeat(HeartbeatPayload heartbeat, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        ArgumentNullException.ThrowIfNull(context);
        var body = Write(WebhookEventTypes.Heartbeat, heartbeat.At, writer =>
        {
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("instance", context.InstanceName);
            writer.WriteString("serviceVersion", heartbeat.ServiceVersion);
            writer.WriteString("directoryState", heartbeat.DirectoryState);
            WriteNullableString(writer, "lastPollAt", heartbeat.LastPollAt is { } lastPoll ? Utc(lastPoll) : null);
            writer.WriteNumber("pendingOutbox", heartbeat.PendingOutbox);
        });
        return new RenderedHttpMessage { Body = body, WebhookEventType = WebhookEventTypes.Heartbeat, Title = "watchdog.heartbeat" };
    }

    /// <summary>The <c>watchdog.test</c> payload of a test send.</summary>
    public static RenderedHttpMessage Test(NotificationContext context, DateTimeOffset sentAt)
    {
        ArgumentNullException.ThrowIfNull(context);
        var body = Write(WebhookEventTypes.Test, sentAt, writer =>
        {
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("instance", context.InstanceName);
            writer.WriteString("message", context.Text.Get("Card.TestTitle"));
        });
        return new RenderedHttpMessage { Body = body, WebhookEventType = WebhookEventTypes.Test, Title = context.Text.Get("Card.TestTitle") };
    }

    private static byte[] Write(string type, DateTimeOffset timestamp, Action<Utf8JsonWriter> writeData)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteString("timestamp", timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            writer.WriteStartObject("data");
            writeData(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteEventData(Utf8JsonWriter writer, WatchEvent e, IReadOnlyList<RuleSettings> rules, NotificationContext context)
    {
        var minimal = context.DetailLevel == DetailLevel.Minimal;
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("eventId", e.EventId.ToString("D", CultureInfo.InvariantCulture));
        writer.WriteNumber("id", e.Id);
        writer.WriteString("kind", e.Kind.ToString());
        writer.WriteString("severity", e.Severity.ToString().ToLowerInvariant());
        WriteNullableString(writer, "domain", e.Domain);
        writer.WriteString("detailLevel", context.DetailLevel.ToString());
        writer.WriteStartArray("rules");
        foreach (var rule in rules)
        {
            writer.WriteStartObject();
            writer.WriteString("id", rule.Id);
            writer.WriteString("name", context.Objects.RuleName(rule));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("tags");
        foreach (var tag in e.Tags)
        {
            writer.WriteStringValue(tag);
        }

        writer.WriteEndArray();
        if (e.Object.Kind == WatchObjectKind.Watchdog)
        {
            writer.WriteStartObject("object");
            writer.WriteString("kind", nameof(WatchObjectKind.Watchdog));
            writer.WriteEndObject();
            WriteHealth(writer, e);
        }
        else
        {
            WriteObject(writer, "object", e.Object);
        }

        WriteNullableString(writer, "previousDn", e.PreviousDn);
        writer.WriteStartArray("changes");
        foreach (var change in e.Changes)
        {
            WriteChange(writer, change, context);
        }

        writer.WriteEndArray();
        if (e.Member is null)
        {
            writer.WriteNull("member");
        }
        else
        {
            WriteObject(writer, "member", e.Member);
        }

        if (e.Via.Count > 0)
        {
            writer.WriteStartArray("via");
            foreach (var via in e.Via)
            {
                writer.WriteStartObject();
                WriteNullableString(writer, "name", via.Name ?? via.Dn);
                if (!minimal)
                {
                    writer.WriteString("dn", via.Dn);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        WriteExtras(writer, e);
        WriteActor(writer, e.Attribution, minimal);
        writer.WriteStartObject("detection");
        writer.WriteString("source", e.Source.ToString());
        writer.WriteString("detectedAt", Utc(e.DetectedAt));
        WriteNullableString(writer, "originatingTime", e.OriginatingTime is { } originating ? Utc(originating) : null);
        WriteNullableString(writer, "originatingDc", e.OriginatingDc);
        writer.WriteEndObject();
        WriteLinks(writer, context.EventLink(e.EventId));
    }

    private static void WriteObject(Utf8JsonWriter writer, string name, ObjectRef reference)
    {
        writer.WriteStartObject(name);
        WriteNullableString(writer, "objectGuid", reference.ObjectGuid?.ToString("D", CultureInfo.InvariantCulture));
        writer.WriteString("dn", reference.Dn);
        writer.WriteString("kind", reference.Kind.ToString());
        WriteNullableString(writer, "sAMAccountName", reference.SamAccountName);
        WriteNullableString(writer, "sid", reference.Sid);
        WriteNullableString(writer, "displayName", reference.DisplayName ?? reference.Name);
        writer.WriteEndObject();
    }

    private static void WriteHealth(Utf8JsonWriter writer, WatchEvent e)
    {
        var code = e.Changes.FirstOrDefault(c => string.Equals(c.Attribute, "code", StringComparison.OrdinalIgnoreCase))?.NewValues.FirstOrDefault()
            ?? WebhookEventTypes.Snake(e.Kind.ToString().Replace("Health", string.Empty, StringComparison.Ordinal));
        var state = e.Severity switch
        {
            >= Severity.High => "Error",
            >= Severity.Low => "Warning",
            _ => "Ok",
        };
        writer.WriteStartObject("health");
        writer.WriteString("code", code);
        writer.WriteString("state", state);
        writer.WriteStartArray("args");
        foreach (var change in e.Changes.Where(c => !string.Equals(c.Attribute, "code", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var value in change.NewValues)
            {
                writer.WriteStringValue(change.Attribute + "=" + value);
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteChange(Utf8JsonWriter writer, AttributeChange change, NotificationContext context)
    {
        writer.WriteStartObject();
        writer.WriteString("attribute", change.Attribute);
        switch (DetailLevelFilter.Visibility(change, context.DetailLevel))
        {
            case ValueVisibility.Secret:
                writer.WriteBoolean("redacted", true);
                break;
            case ValueVisibility.Hidden:
                writer.WriteBoolean("valueHidden", true);
                break;
            case ValueVisibility.Visible:
                WriteValues(writer, "before", change.OldValues, change.OldCount);
                WriteValues(writer, "after", change.NewValues, change.NewCount);
                if (change.Added.Count > 0 || change.Removed.Count > 0)
                {
                    WriteValues(writer, "added", change.Added, change.Added.Count);
                    WriteValues(writer, "removed", change.Removed, change.Removed.Count);
                }

                if (change.FlagsAdded.Count > 0 || change.FlagsRemoved.Count > 0)
                {
                    WriteStrings(writer, "flagsAdded", change.FlagsAdded);
                    WriteStrings(writer, "flagsRemoved", change.FlagsRemoved);
                }

                if (change.Truncated)
                {
                    writer.WriteBoolean("truncated", true);
                }

                break;
        }

        writer.WriteEndObject();
    }

    /// <summary>At most the first <see cref="AlertContentBuilder.MaxValuesShown"/> values plus the exact count when more exist.</summary>
    private static void WriteValues(Utf8JsonWriter writer, string name, IReadOnlyList<string> values, int count)
    {
        WriteStrings(writer, name, values.Take(AlertContentBuilder.MaxValuesShown));
        var total = Math.Max(count, values.Count);
        if (total > AlertContentBuilder.MaxValuesShown || total > values.Count)
        {
            writer.WriteNumber(name + "Count", total);
        }
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteExtras(Utf8JsonWriter writer, WatchEvent e)
    {
        if (e.Expiry is { } expiry)
        {
            writer.WriteStartObject("expiry");
            writer.WriteString("kind", expiry.Kind.ToString());
            writer.WriteString("expiresAt", Utc(expiry.ExpiresAt));
            writer.WriteString("lastValidDay", expiry.LastValidDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteNumber("daysLeft", expiry.DaysLeft);
            writer.WriteNumber("thresholdDays", expiry.ThresholdDays);
            writer.WriteEndObject();
        }

        if (e.Lockout is { } lockout)
        {
            writer.WriteStartObject("lockout");
            writer.WriteString("lockedAt", Utc(lockout.LockedAt));
            WriteNullableString(writer, "lockedUntil", lockout.LockedUntil is { } until ? Utc(until) : null);
            WriteNullableString(writer, "callerComputer", lockout.CallerComputer);
            writer.WriteNumber("lockoutsToday", lockout.LockoutsToday);
            writer.WriteStartArray("sources");
            foreach (var source in lockout.Sources)
            {
                writer.WriteStartObject();
                WriteNullableString(writer, "ipAddress", source.IpAddress);
                WriteNullableString(writer, "workstation", source.Workstation);
                writer.WriteNumber("count", source.Count);
                writer.WriteNumber("eventCode", source.EventCode);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (e.Burst is { } burst)
        {
            writer.WriteStartObject("burst");
            writer.WriteNumber("count", burst.Count);
            writer.WriteNumber("windowSeconds", (long)burst.Window.TotalSeconds);
            writer.WriteStartArray("objects");
            foreach (var reference in burst.Objects)
            {
                writer.WriteStringValue(reference.SamAccountName ?? reference.Name ?? reference.Dn);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    private static void WriteActor(Utf8JsonWriter writer, AdMng.Watchdog.Contracts.Events.Attribution attribution, bool minimal)
    {
        writer.WriteStartObject("actor");
        writer.WriteString("status", attribution.Status.ToString());
        if (!minimal)
        {
            writer.WriteString("confidence", attribution.Confidence.ToString());
        }

        WriteNullableString(writer, "account", attribution.Actor?.Account);
        WriteNullableString(writer, "sid", attribution.Actor?.Sid);
        if (!minimal)
        {
            WriteNullableString(writer, "workstation", attribution.Workstation);
            WriteNullableString(writer, "ipAddress", attribution.IpAddress);
            WriteNullableString(writer, "reason", attribution.Reason);
            writer.WriteStartArray("evidence");
            foreach (var evidence in attribution.Evidence)
            {
                writer.WriteStartObject();
                writer.WriteString("dc", evidence.Dc);
                writer.WriteString("log", evidence.Log);
                writer.WriteNumber("eventCode", evidence.EventCode);
                writer.WriteNumber("recordId", evidence.RecordId);
                writer.WriteString("timeCreated", evidence.TimeCreated.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteLinks(Utf8JsonWriter writer, string? eventLink)
    {
        writer.WriteStartObject("links");
        WriteNullableString(writer, "event", eventLink);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
