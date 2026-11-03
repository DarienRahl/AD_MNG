using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace AdMng.Core.EventLogs;

/// <summary>
/// Event XML (<c>EventRecord.ToXml()</c>, the records of an <c>.evtx</c> export) → <see cref="EventLogRecord"/>
/// (design note e-event-logs.md 3.1). <c>System</c>: <c>Provider/@Name</c>, <c>EventID</c> (the element value; the
/// <c>Qualifiers</c> attribute of classic sources is ignored), <c>Version</c>, <c>Level</c>, <c>Task</c>,
/// <c>Opcode</c>, <c>Keywords</c> (<c>0x…</c>), <c>TimeCreated/@SystemTime</c> (UTC; more than 7 fractional digits
/// are truncated), <c>EventRecordID</c>, <c>Correlation/@ActivityID</c>, <c>Channel</c>, <c>Computer</c> and
/// <c>Security/@UserID</c>. Values: <c>EventData/Data</c> by <c>@Name</c> in document order (an unnamed <c>Data</c> is
/// <c>Data</c><i>n</i>, n = its position among the unnamed ones), or – when there is no <c>EventData</c> – the children
/// of <c>UserData/*</c> (nested simple elements as <c>Parent.Child</c>); <c>-</c> and empty values are omitted and the
/// documented <c>%%NNNN</c> codes decoded (<see cref="InsertionStringDecoder"/>). <c>RenderingInfo</c> is ignored.
/// </summary>
/// <remarks>
/// Hardened like the Watchdog's <c>SecurityEventParser</c>: DTDs prohibited, no resolver, comments and processing
/// instructions ignored, at most <see cref="MaxCharacters"/> characters nested at most <see cref="MaxDepth"/> levels.
/// <c>CheckCharacters</c> is off – a value an attacker wrote with a control character (rendered raw or as
/// <c>&amp;#x1;</c>) does not drop the record – and every character that is invalid in XML 1.0 is replaced by
/// <see cref="InvalidCharacterReplacement"/>. Malformed records or
/// records without <c>EventID</c>, <c>EventRecordID</c> or <c>TimeCreated</c> yield false; the parser never throws for
/// bad XML and never surfaces the exception text (it can contain record content).
/// </remarks>
public static class EventXmlParser
{
    /// <summary>The event schema namespace.</summary>
    public const string EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>The longest event XML accepted (characters).</summary>
    public const int MaxCharacters = 4 * 1024 * 1024;

    /// <summary>Replacement of characters that are not allowed in XML 1.0.</summary>
    public const char InvalidCharacterReplacement = '�';

    /// <summary>Prefix of the names given to unnamed <c>EventData/Data</c> values (<c>Data1</c>, <c>Data2</c>, …).</summary>
    public const string UnnamedDataPrefix = "Data";

    /// <summary>Element nesting accepted (real records nest about six levels; <c>XElement.Value</c> recurses).</summary>
    public const int MaxDepth = 32;

    /// <summary>Nesting levels below the <c>UserData</c> type element that are flattened.</summary>
    private const int MaxUserDataDepth = 4;

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false,
        MaxCharactersInDocument = MaxCharacters,
    };

    /// <summary>Parses one record (see the class remarks).</summary>
    /// <param name="xml">The event XML (one <c>Event</c> element).</param>
    /// <param name="host">Where it was read: the DC or computer FQDN, or the file name (<see cref="EventLogRecord.Host"/>).</param>
    /// <param name="record">The record, when the XML is a well-formed event.</param>
    /// <exception cref="ArgumentException"><paramref name="host"/> is empty.</exception>
    public static bool TryParse(string? xml, string host, [NotNullWhen(true)] out EventLogRecord? record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        record = null;
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaxCharacters)
        {
            return false;
        }

        XElement root;
        try
        {
            var clean = Clean(xml);
            if (!WithinDepth(clean))
            {
                return false;
            }

            using var reader = XmlReader.Create(new StringReader(clean), ReaderSettings);
            root = XElement.Load(reader);
        }
        catch (XmlException)
        {
            return false;   // the message can contain record content – never surfaced
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (root.Name.LocalName != "Event" || !IsEventNamespace(root.Name.Namespace))
        {
            return false;
        }

        var system = Child(root, "System");
        if (system is null
            || !int.TryParse(Child(system, "EventID")?.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var eventId)
            || !long.TryParse(Child(system, "EventRecordID")?.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var recordId)
            || !TryParseSystemTime(Child(system, "TimeCreated")?.Attribute("SystemTime")?.Value, out var timeCreated))
        {
            return false;
        }

        record = new EventLogRecord
        {
            Host = host,
            Computer = Text(Child(system, "Computer")?.Value) ?? string.Empty,
            Channel = Text(Child(system, "Channel")?.Value) ?? string.Empty,
            Provider = Text(Child(system, "Provider")?.Attribute("Name")?.Value),
            EventId = eventId,
            Version = ParseInt(Child(system, "Version")?.Value) ?? 0,
            Level = byte.TryParse(Child(system, "Level")?.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var level) ? level : null,
            Task = ParseInt(Child(system, "Task")?.Value),
            Opcode = ParseInt(Child(system, "Opcode")?.Value),
            Keywords = ParseKeywords(Child(system, "Keywords")?.Value),
            RecordId = recordId,
            TimeCreated = timeCreated,
            ActivityId = Guid.TryParse(Child(system, "Correlation")?.Attribute("ActivityID")?.Value, out var activity) ? activity : null,
            UserSid = Text(Child(system, "Security")?.Attribute("UserID")?.Value),
            Data = ReadData(root),
        };
        return true;
    }

    /// <summary>Parses one record; null when <see cref="TryParse"/> returns false.</summary>
    /// <exception cref="ArgumentException"><paramref name="host"/> is empty.</exception>
    public static EventLogRecord? Parse(string? xml, string host) => TryParse(xml, host, out var record) ? record : null;

    /// <summary>Parses <c>TimeCreated/@SystemTime</c> (ISO 8601, up to 9 fractional digits, more than 7 truncated) as UTC.</summary>
    public static bool TryParseSystemTime(string? value, out DateTimeOffset utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return false;
        }

        var text = value.Trim();
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            var end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            var fraction = text[(dot + 1)..end];
            text = text[..(dot + 1)] + (fraction.Length > 7 ? fraction[..7] : fraction) + text[end..];
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return false;
        }

        utc = parsed.ToUniversalTime();
        return true;
    }

    /// <summary>A first, streaming pass: no element nests deeper than <see cref="MaxDepth"/>.</summary>
    /// <exception cref="XmlException">The XML is malformed.</exception>
    private static bool WithinDepth(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings);
        while (reader.Read())
        {
            if (reader.Depth > MaxDepth)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEventNamespace(XNamespace ns) => ns == XNamespace.None || ns.NamespaceName == EventNamespace;

    private static XElement? Child(XElement parent, string localName)
    {
        foreach (var element in parent.Elements())
        {
            if (element.Name.LocalName == localName)
            {
                return element;
            }
        }

        return null;
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary><c>0x8020000000000000</c> (or decimal) → the 64-bit pattern as a long (the high bit makes it negative).</summary>
    private static long ParseKeywords(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) ? unchecked((long)hex) : 0;
        }

        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? unchecked((long)number) : 0;
    }

    private static List<KeyValuePair<string, string>> ReadData(XElement root)
    {
        var data = new List<KeyValuePair<string, string>>();
        if (Child(root, "EventData") is { } eventData)
        {
            var unnamed = 0;
            foreach (var item in eventData.Elements())
            {
                if (item.Name.LocalName != "Data")
                {
                    continue;   // Binary, ComplexData
                }

                var name = Text(item.Attribute("Name")?.Value);
                if (name is null)
                {
                    unnamed++;
                    name = UnnamedDataPrefix + unnamed.ToString(CultureInfo.InvariantCulture);
                }

                Add(data, name, item.Value);
            }

            return data;
        }

        // Event-log service records (1102 LogFileCleared, …) and other manifest events carry their fields under
        // UserData/<Type>/<Field>.
        foreach (var container in Child(root, "UserData")?.Elements() ?? [])
        {
            AddUserData(data, container, prefix: null, depth: 0);
        }

        return data;
    }

    private static void AddUserData(List<KeyValuePair<string, string>> data, XElement container, string? prefix, int depth)
    {
        foreach (var field in container.Elements())
        {
            var local = field.Name.LocalName == "Data" && Text(field.Attribute("Name")?.Value) is { } dataName ? dataName : field.Name.LocalName;
            var name = prefix is null ? local : prefix + "." + local;
            if (!field.HasElements)
            {
                Add(data, name, field.Value);
            }
            else if (depth + 1 < MaxUserDataDepth)
            {
                AddUserData(data, field, name, depth + 1);
            }
        }
    }

    private static void Add(List<KeyValuePair<string, string>> data, string name, string? raw)
    {
        var value = Text(raw);
        if (value is null || value == "-")
        {
            return;
        }

        data.Add(new KeyValuePair<string, string>(Clean(name), InsertionStringDecoder.Decode(value)!));
    }

    /// <summary>Trimmed, invalid characters replaced; null when empty.</summary>
    private static string? Text(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = Clean(value).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary><paramref name="value"/> with every character that is invalid in XML 1.0 replaced by
    /// <see cref="InvalidCharacterReplacement"/>.</summary>
    private static string Clean(string value)
    {
        char[]? chars = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (XmlConvert.IsXmlChar(c))
            {
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], c))
            {
                i++;
                continue;
            }

            chars ??= value.ToCharArray();
            chars[i] = InvalidCharacterReplacement;
        }

        return chars is null ? value : new string(chars);
    }
}
