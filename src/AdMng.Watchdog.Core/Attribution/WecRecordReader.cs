using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// One line of an OpenWEC <c>RawJson</c> output file (stage B design 7.6): <c>{"meta": {"IpAddress", "TimeReceived",
/// "Principal" (deprecated upstream, same value as Client), "Client", "Subscription": {"Uuid", "Version", "Name", "Uri",
/// …}, "Node"}, "data": "&lt;Event …&gt;…&lt;/Event&gt;"}</c> – the field order of the pinned OpenWEC
/// (<c>server/src/formats/raw_json.rs</c>, serde in declaration order).
/// </summary>
/// <param name="IpAddress"><c>meta.IpAddress</c>: the forwarder's address as OpenWEC saw it.</param>
/// <param name="TimeReceived"><c>meta.TimeReceived</c> (RFC 3339); null when absent or unreadable.</param>
/// <param name="Client"><c>meta.Client</c>: the Kerberos principal of the forwarder, e.g. <c>DC01$@CONTOSO.COM</c>.</param>
/// <param name="Principal"><c>meta.Principal</c> (deprecated upstream, same value).</param>
/// <param name="SubscriptionUuid"><c>meta.Subscription.Uuid</c>; null when absent or not a GUID.</param>
/// <param name="SubscriptionName"><c>meta.Subscription.Name</c>.</param>
/// <param name="Data">The raw event XML (<c>content_format = Raw</c>).</param>
public sealed record WecRecord(
    string? IpAddress,
    DateTimeOffset? TimeReceived,
    string? Client,
    string? Principal,
    Guid? SubscriptionUuid,
    string? SubscriptionName,
    string Data)
{
    /// <summary><see cref="Client"/>, or <see cref="Principal"/> only when Client is absent (7.6 check 2).</summary>
    public string? EffectiveClient => Client ?? Principal;
}

/// <summary>
/// Reads one RawJson line (stage B design 7.6) with <see cref="Utf8JsonReader"/> – no DOM, no dynamic types: unknown
/// properties are skipped, the nesting is limited, duplicate properties keep the last value. A line that does not parse
/// and contains the record start token <see cref="RecordStart"/> more than once is retried from its last occurrence:
/// when OpenWEC died in the middle of a write, its next record is appended to the partial line (7.5 "resynchronises").
/// </summary>
public static class WecRecordReader
{
    /// <summary>The first bytes of every record (serde writes <c>meta</c> first, without whitespace).</summary>
    public const string RecordStart = "{\"meta\":";

    private static readonly byte[] RecordStartBytes = Encoding.UTF8.GetBytes(RecordStart);

    private static readonly JsonReaderOptions Options = new() { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow };

    /// <summary>Parses <paramref name="line"/> (without the line feed); null when it is not a record. <paramref name="resynchronized"/>
    /// is true when the record was found after a partial one.</summary>
    public static WecRecord? Read(ReadOnlySpan<byte> line, out bool resynchronized)
    {
        resynchronized = false;
        if (TryRead(line, out var record))
        {
            return record;
        }

        var last = line.LastIndexOf(RecordStartBytes);
        if (last > 0 && TryRead(line[last..], out record))
        {
            resynchronized = true;
            return record;
        }

        return null;
    }

    /// <summary>Parses <paramref name="line"/> (see <see cref="Read(ReadOnlySpan{byte}, out bool)"/>).</summary>
    public static WecRecord? Read(ReadOnlySpan<byte> line) => Read(line, out _);

    private static bool TryRead(ReadOnlySpan<byte> line, out WecRecord? record)
    {
        record = null;
        try
        {
            var reader = new Utf8JsonReader(line, Options);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            Meta meta = default;
            string? data = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("meta"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        return false;
                    }

                    meta = ReadMeta(ref reader);
                }
                else if (reader.ValueTextEquals("data"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        return false;
                    }

                    data = reader.GetString();
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || string.IsNullOrEmpty(data))
            {
                return false;   // trailing content, a truncated object or no event
            }

            record = new WecRecord(meta.IpAddress, meta.TimeReceived, meta.Client, meta.Principal, meta.SubscriptionUuid, meta.SubscriptionName, data);
            return true;
        }
        catch (JsonException)
        {
            return false;   // the message may contain record content – never surfaced
        }
        catch (InvalidOperationException)
        {
            return false;   // a value of an unexpected type (GetString on a number)
        }
    }

    private static Meta ReadMeta(ref Utf8JsonReader reader)
    {
        var meta = default(Meta);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("IpAddress"u8))
            {
                meta.IpAddress = ReadString(ref reader);
            }
            else if (reader.ValueTextEquals("TimeReceived"u8))
            {
                var text = ReadString(ref reader);
                meta.TimeReceived = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                    ? time.ToUniversalTime()
                    : null;
            }
            else if (reader.ValueTextEquals("Client"u8))
            {
                meta.Client = ReadString(ref reader);
            }
            else if (reader.ValueTextEquals("Principal"u8))
            {
                meta.Principal = ReadString(ref reader);
            }
            else if (reader.ValueTextEquals("Subscription"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("Uuid"u8))
                    {
                        meta.SubscriptionUuid = Guid.TryParse(ReadString(ref reader), out var uuid) ? uuid : null;
                    }
                    else if (reader.ValueTextEquals("Name"u8))
                    {
                        meta.SubscriptionName = ReadString(ref reader);
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return meta;
    }

    /// <summary>The next value as a string; null for JSON null (other types throw → the line is malformed).</summary>
    private static string? ReadString(ref Utf8JsonReader reader)
    {
        reader.Read();
        return reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
    }

    private struct Meta
    {
        public string? IpAddress;
        public DateTimeOffset? TimeReceived;
        public string? Client;
        public string? Principal;
        public Guid? SubscriptionUuid;
        public string? SubscriptionName;
    }
}
