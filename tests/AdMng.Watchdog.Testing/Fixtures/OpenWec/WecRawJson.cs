using System.Globalization;
using System.Text;

namespace AdMng.Watchdog.Testing.Fixtures.OpenWec;

/// <summary>
/// OpenWEC <c>RawJson</c> output lines exactly as the pinned OpenWEC (commit 86de07f, <c>server/src/formats/raw_json.rs</c>)
/// writes them: serde field order <c>{"meta":{"IpAddress","TimeReceived","Principal","Client","Subscription":{"Uuid",
/// "Version","Name"}},"data":"…"}</c> (<c>Uri</c>, <c>ClientRevision</c>, <c>ServerRevision</c> and <c>Node</c> are
/// omitted when unset), <c>TimeReceived</c> in chrono's RFC 3339 form with nanoseconds and <c>+00:00</c>, the uuid in
/// lower case, the event XML as a JSON string escaped like serde_json (only <c>"</c>, <c>\</c> and control characters;
/// non-ASCII stays UTF-8), one record per line ending in <c>\n</c>.
/// </summary>
public static class WecRawJson
{
    /// <summary>The shipped subscription uuid (<c>Watchdog:Collector:SubscriptionUuid</c>).</summary>
    public static readonly Guid SubscriptionUuid = Guid.Parse("4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10");

    /// <summary>A public version as OpenWEC derives it (lower case).</summary>
    public const string SubscriptionVersion = "d2fa1803-b284-f1b2-d2fa-1803b284f1b2";

    /// <summary>One line (with its line feed) for <paramref name="eventXml"/> received from <paramref name="client"/>.</summary>
    public static string Line(string eventXml, string client, string ipAddress, DateTimeOffset timeReceived, Guid? subscriptionUuid = null,
        string subscriptionName = "admng-dc-security", string? principal = null, string? node = null)
    {
        ArgumentNullException.ThrowIfNull(eventXml);
        var builder = new StringBuilder();
        builder.Append("{\"meta\":{\"IpAddress\":").Append(Escape(ipAddress))
            .Append(",\"TimeReceived\":").Append(Escape(Rfc3339(timeReceived)))
            .Append(",\"Principal\":").Append(Escape(principal ?? client))
            .Append(",\"Client\":").Append(Escape(client))
            .Append(",\"Subscription\":{\"Uuid\":").Append(Escape((subscriptionUuid ?? SubscriptionUuid).ToString("D", CultureInfo.InvariantCulture)))
            .Append(",\"Version\":").Append(Escape(SubscriptionVersion))
            .Append(",\"Name\":").Append(Escape(subscriptionName)).Append('}');
        if (node is not null)
        {
            builder.Append(",\"Node\":").Append(Escape(node));
        }

        builder.Append("},\"data\":").Append(Escape(eventXml)).Append("}\n");
        return builder.ToString();
    }

    /// <summary>chrono <c>DateTime&lt;Utc&gt;::to_rfc3339()</c>: nanoseconds when not zero, offset <c>+00:00</c>.</summary>
    public static string Rfc3339(DateTimeOffset time)
    {
        var utc = time.UtcDateTime;
        var nanos = utc.Ticks % TimeSpan.TicksPerSecond * 100;
        var seconds = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return nanos == 0 ? seconds + "+00:00" : string.Create(CultureInfo.InvariantCulture, $"{seconds}.{nanos:D9}+00:00");
    }

    /// <summary>A JSON string as serde_json writes it.</summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    /// <summary>The OpenWEC client directory of a principal (<c>DC01$@CONTOSO.TEST</c> → <c>DC01@CONTOSO.TEST</c>).</summary>
    public static string ClientDirectory(string principal) =>
        new([.. principal.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '@')]);
}
