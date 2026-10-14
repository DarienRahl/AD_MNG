using System.Globalization;
using System.Text;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Service.Attribution;

/// <summary>
/// <see cref="SecurityEventQuery"/> → structured Event Log query XML (<c>QueryList</c> with a Select and, for
/// <see cref="SecurityEventQuery.SuppressAttributes"/>, a Suppress element). Values are XPath string literals (a value
/// containing both quote characters is refused), the log and field names plain identifiers, and the query text is
/// XML-escaped – no value can change the query.
/// </summary>
public static class SecurityEventXPath
{
    /// <summary>The query XML for <paramref name="query"/>.</summary>
    /// <exception cref="ArgumentException">A value cannot be expressed as an XPath literal, or a name is not a plain identifier.</exception>
    public static string Build(SecurityEventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RequireIdentifier(query.LogName, nameof(query));
        var system = new List<string>();
        if (query.EventCodes.Count > 0)
        {
            system.Add("(" + string.Join(" or ", query.EventCodes.Select(c => string.Create(CultureInfo.InvariantCulture, $"EventID={c}"))) + ")");
        }

        if (query.AfterRecordId is { } after)
        {
            system.Add(string.Create(CultureInfo.InvariantCulture, $"(EventRecordID > {after})"));
        }

        if (query.From is { } from || query.To is not null)
        {
            var bounds = new List<string>();
            if (query.From is { } start)
            {
                bounds.Add($"@SystemTime >= '{Time(start)}'");
            }

            if (query.To is { } end)
            {
                bounds.Add($"@SystemTime <= '{Time(end)}'");
            }

            system.Add("TimeCreated[" + string.Join(" and ", bounds) + "]");
        }

        var select = new StringBuilder("*");
        if (system.Count > 0)
        {
            select.Append("[System[").Append(string.Join(" and ", system)).Append("]]");
        }

        foreach (var (name, value) in query.DataEquals)
        {
            RequireIdentifier(name, nameof(query));
            select.Append("[EventData[Data[@Name='").Append(name).Append("']=").Append(Literal(value)).Append("]]");
        }

        var xml = new StringBuilder();
        var log = query.LogName;
        xml.Append("<QueryList><Query Id=\"0\" Path=\"").Append(log).Append("\">");
        xml.Append("<Select Path=\"").Append(log).Append("\">").Append(EscapeText(select.ToString())).Append("</Select>");
        if (query.SuppressAttributes.Count > 0)
        {
            var suppress = "*[EventData[" + string.Join(" or ", query.SuppressAttributes.Select(a => "Data[@Name='AttributeLDAPDisplayName']=" + Literal(a))) + "]]";
            xml.Append("<Suppress Path=\"").Append(log).Append("\">").Append(EscapeText(suppress)).Append("</Suppress>");
        }

        xml.Append("</Query></QueryList>");
        return xml.ToString();
    }

    /// <summary>XML text escaping (&amp;, &lt;, &gt;); quotes need no escaping in element text.</summary>
    private static string EscapeText(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains('\'', StringComparison.Ordinal))
        {
            return "'" + value + "'";
        }

        if (!value.Contains('"', StringComparison.Ordinal))
        {
            return "\"" + value + "\"";
        }

        throw new ArgumentException("A filter value contains both quote characters.", nameof(value));
    }

    private static void RequireIdentifier(string value, string parameter)
    {
        if (string.IsNullOrEmpty(value) || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '/' or ' '))
        {
            throw new ArgumentException("A log or field name is not a plain identifier.", parameter);
        }
    }
}
