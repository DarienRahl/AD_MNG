using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Event XML (<c>EventRecord.ToXml()</c>) → <see cref="SecurityEvent"/> (section 11.3): <c>System/EventID</c>,
/// <c>Version</c>, <c>TimeCreated/@SystemTime</c> (UTC; more than 7 fractional digits are truncated),
/// <c>EventRecordID</c>, <c>Computer</c> and <c>Provider/@Name</c>; <c>EventData/Data</c> <b>by name, never by
/// position</b>; for event-log service records (1102) the children of <c>UserData/*</c>. <c>-</c> and empty values are
/// omitted, <c>%%</c> codes decoded (<see cref="InsertionStringDecoder"/>); for 4740 <c>TargetDomainName</c> (the caller
/// computer) is also exposed as <see cref="CallerComputerName"/>. Unknown names and higher versions are kept silently;
/// DTDs are prohibited and malformed records yield null (the exception text, which can contain directory values, is
/// never surfaced). Characters that are invalid in XML 1.0 (an attacker-written <c>displayName</c> "x\u0001", rendered
/// raw or as <c>&amp;#x1;</c>) do not drop the record: they are replaced by U+FFFD in the stored values.
/// </summary>
public sealed class SecurityEventParser
{
    /// <summary>The event schema namespace.</summary>
    public const string EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>Data name under which the 4740 caller computer (logged as TargetDomainName) is exposed.</summary>
    public const string CallerComputerName = "CallerComputerName";

    private static readonly XNamespace Ns = EventNamespace;

    // CheckCharacters off: EventRecord.ToXml() renders control characters of attacker-written values (displayName
    // "x\u0001") as &#x1; or raw – rejecting the record would let one value hide the whole change from attribution. Such
    // characters are replaced in the values (Clean). DTDs stay prohibited, no resolver.
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false,
        MaxCharactersInDocument = 4 * 1024 * 1024,
    };

    /// <summary>Replacement of characters that are not allowed in XML 1.0 (control characters other than tab, line feed and
    /// carriage return, lone surrogates, U+FFFE/U+FFFF).</summary>
    public const char InvalidCharacterReplacement = '�';

    /// <summary>Parses one record; null when the XML is malformed or misses EventID, EventRecordID, TimeCreated or
    /// Computer.</summary>
    /// <param name="xml">The event XML.</param>
    /// <param name="source">Cursor key of the source (DC FQDN or WEF collector).</param>
    public ParsedSecurityRecord? Parse(string? xml, string source) => TryParse(xml, source, out var record) ? record : null;

    /// <summary>Parses one record (see <see cref="Parse"/>).</summary>
    public bool TryParse(string? xml, string source, [NotNullWhen(true)] out ParsedSecurityRecord? record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        record = null;
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(Clean(xml)!), ReaderSettings);   // raw invalid characters too
            root = XElement.Load(reader);
        }
        catch (XmlException)
        {
            return false;   // message may contain record content – never logged
        }

        var system = root.Element(Ns + "System");
        if (system is null
            || !int.TryParse(system.Element(Ns + "EventID")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var eventCode)
            || !long.TryParse(system.Element(Ns + "EventRecordID")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var recordId)
            || !TryParseSystemTime(system.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value, out var timeCreated)
            || Clean(system.Element(Ns + "Computer")?.Value)?.Trim() is not { Length: > 0 } computer)
        {
            return false;
        }

        _ = int.TryParse(system.Element(Ns + "Version")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var version);
        var provider = system.Element(Ns + "Provider")?.Attribute("Name")?.Value ?? string.Empty;
        var data = ReadData(root, eventCode);
        var channel = Clean(system.Element(Ns + "Channel")?.Value)?.Trim();
        record = new ParsedSecurityRecord(provider, new SecurityEvent
        {
            Source = source,
            Computer = computer,
            RecordId = recordId,
            EventCode = eventCode,
            Version = version,
            TimeCreated = timeCreated,
            Data = data,
        }, string.IsNullOrEmpty(channel) ? null : channel);
        return true;
    }

    /// <summary>Parses <c>TimeCreated/@SystemTime</c> (ISO 8601, up to 9 fractional digits) as UTC.</summary>
    public static bool TryParseSystemTime(string? value, out DateTimeOffset utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(value))
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

    private static Dictionary<string, string> ReadData(XElement root, int eventCode)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in root.Element(Ns + "EventData")?.Elements(Ns + "Data") ?? [])
        {
            if (item.Attribute("Name")?.Value is { Length: > 0 } name)
            {
                Add(data, name, item.Value);
            }
        }

        // Event-log service records (1102 LogFileCleared, …) carry their fields under UserData/<Type>/<Field>.
        foreach (var container in root.Element(Ns + "UserData")?.Elements() ?? [])
        {
            foreach (var field in container.Elements())
            {
                Add(data, field.Name.LocalName, field.Value);
            }
        }

        if (eventCode == 4740 && data.TryGetValue("TargetDomainName", out var caller))
        {
            data[CallerComputerName] = caller;
        }

        return data;
    }

    private static void Add(Dictionary<string, string> data, string name, string? raw)
    {
        var value = Clean(raw)?.Trim();
        if (string.IsNullOrEmpty(value) || value == "-")
        {
            return;
        }

        data[Clean(name)!] = InsertionStringDecoder.Decode(value)!;
    }

    /// <summary><paramref name="value"/> with every character that is invalid in XML 1.0 replaced by
    /// <see cref="InvalidCharacterReplacement"/> (stored values stay printable and serializable).</summary>
    private static string? Clean(string? value)
    {
        if (value is null)
        {
            return null;
        }

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
