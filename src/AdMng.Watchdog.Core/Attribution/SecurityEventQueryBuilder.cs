using System.Globalization;
using System.Text;
using System.Xml;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// <see cref="SecurityEventQuery"/> → Windows structured query XML (section 11.1), e.g.
/// <c>&lt;QueryList&gt;&lt;Query Id="0" Path="Security"&gt;&lt;Select Path="Security"&gt;*[System[(EventID=4720 or …) and
/// EventRecordID &amp;gt; 12345]]&lt;/Select&gt;…&lt;Suppress Path="Security"&gt;*[System[EventID=5136] and
/// EventData[Data[@Name='AttributeLDAPDisplayName']='dSCorePropagationData']]&lt;/Suppress&gt;&lt;/Query&gt;&lt;/QueryList&gt;</c>.
/// At most <see cref="MaxEventIdsPerSelect"/> EventID expressions per Select (several Selects are OR-ed); time filters
/// <c>TimeCreated[@SystemTime&gt;='…Z']</c>; data filters <c>EventData[Data[@Name='TargetLogonId']='0x30dc2']</c>.
/// Every name and value is restricted to <c>[0-9A-Za-z.\-_:{}$ ]</c> (anything else is rejected – no XPath injection)
/// and the XML is written by an <see cref="XmlWriter"/> (escaping).
/// </summary>
public static class SecurityEventQueryBuilder
{
    /// <summary>EventID expressions per Select (the event-log service limits the expression complexity).</summary>
    public const int MaxEventIdsPerSelect = 20;

    /// <summary>The query XML of <paramref name="query"/>.</summary>
    /// <exception cref="ArgumentException">A log name, data name, data value or suppressed attribute contains a
    /// character outside <c>[0-9A-Za-z.\-_:{}$ ]</c>.</exception>
    public static string Build(SecurityEventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Require(query.LogName, nameof(query.LogName));
        var common = CommonConditions(query);
        var dataConditions = DataConditions(query);
        var codes = query.EventCodes.Distinct().ToList();
        var chunks = codes.Count == 0 ? [[]] : codes.Chunk(MaxEventIdsPerSelect).ToList();

        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false }))
        {
            writer.WriteStartElement("QueryList");
            writer.WriteStartElement("Query");
            writer.WriteAttributeString("Id", "0");
            writer.WriteAttributeString("Path", query.LogName);
            foreach (var chunk in chunks)
            {
                writer.WriteStartElement("Select");
                writer.WriteAttributeString("Path", query.LogName);
                writer.WriteString(SelectExpression(chunk, common, dataConditions));
                writer.WriteEndElement();
            }

            foreach (var attribute in query.SuppressAttributes.Distinct(StringComparer.Ordinal))
            {
                Require(attribute, nameof(query.SuppressAttributes));
                writer.WriteStartElement("Suppress");
                writer.WriteAttributeString("Path", query.LogName);
                writer.WriteString(string.Create(CultureInfo.InvariantCulture,
                    $"*[System[EventID={SecurityEventCatalog.ObjectModified}] and EventData[Data[@Name='AttributeLDAPDisplayName']='{attribute}']]"));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return builder.ToString();
    }

    /// <summary>True when <paramref name="value"/> may appear in a query (<c>[0-9A-Za-z.\-_:{}$ ]</c>, not empty).</summary>
    public static bool IsAllowedValue(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '{' or '}' or '$' or ' ');

    private static string SelectExpression(int[] codes, List<string> common, List<string> dataConditions)
    {
        var system = new List<string>();
        if (codes.Length > 0)
        {
            system.Add("(" + string.Join(" or ", codes.Select(c => string.Create(CultureInfo.InvariantCulture, $"EventID={c}"))) + ")");
        }

        system.AddRange(common);
        var parts = new List<string>();
        if (system.Count > 0)
        {
            parts.Add("System[" + string.Join(" and ", system) + "]");
        }

        parts.AddRange(dataConditions);
        return parts.Count == 0 ? "*" : "*[" + string.Join(" and ", parts) + "]";
    }

    private static List<string> CommonConditions(SecurityEventQuery query)
    {
        var conditions = new List<string>();
        if (query.AfterRecordId is { } after)
        {
            conditions.Add(string.Create(CultureInfo.InvariantCulture, $"EventRecordID > {after}"));
        }

        if (query.From is { } from)
        {
            conditions.Add($"TimeCreated[@SystemTime>='{SystemTime(from)}']");
        }

        if (query.To is { } to)
        {
            conditions.Add($"TimeCreated[@SystemTime<='{SystemTime(to)}']");
        }

        return conditions;
    }

    private static List<string> DataConditions(SecurityEventQuery query)
    {
        var conditions = new List<string>();
        foreach (var (name, value) in query.DataEquals)
        {
            Require(name, nameof(query.DataEquals));
            Require(value, nameof(query.DataEquals));
            conditions.Add($"EventData[Data[@Name='{name}']='{value}']");
        }

        return conditions;
    }

    private static string SystemTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static void Require(string? value, string parameter)
    {
        if (!IsAllowedValue(value))
        {
            throw new ArgumentException("A query name or value contains characters outside [0-9A-Za-z.-_:{}$ ].", parameter);
        }
    }
}
