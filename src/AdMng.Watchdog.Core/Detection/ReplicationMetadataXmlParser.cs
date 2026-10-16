using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Parser of the Windows constructed attribute <c>msDS-ReplAttributeMetaData</c> (one XML document per attribute,
/// section 10.10). Windows may append a NUL character and leaves <c>&amp;</c> in DNs unescaped – both are repaired before
/// parsing with a hardened <see cref="XmlReader"/> (no DTD, no resolver). Never throws for malformed values.
/// </summary>
public static partial class ReplicationMetadataXmlParser
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = 64 * 1024,
    };

    /// <summary>Parses all values; malformed ones are skipped and counted in <paramref name="rejected"/>.</summary>
    public static ReplicationMetadata Parse(IEnumerable<string> values, out int rejected)
    {
        ArgumentNullException.ThrowIfNull(values);
        rejected = 0;
        var entries = new List<AttributeMetadata>();
        foreach (var value in values)
        {
            if (TryParse(value, out var entry))
            {
                entries.Add(entry!);
            }
            else
            {
                rejected++;
            }
        }

        return new ReplicationMetadata(entries);
    }

    /// <summary>Parses one <c>&lt;DS_REPL_ATTR_META_DATA&gt;</c> value.</summary>
    public static bool TryParse(string? value, out AttributeMetadata? metadata)
    {
        metadata = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var sanitized = UnescapedAmpersand().Replace(value.Replace("\0", string.Empty, StringComparison.Ordinal), "&amp;");
        string? name = null, dsaDn = null;
        int? version = null;
        DateTimeOffset? time = null;
        Guid? invocationId = null;
        long? usnOriginating = null, usnLocal = null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(sanitized), ReaderSettings);
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1)
                {
                    reader.Read();
                    continue;
                }

                // ReadElementContentAsString moves to the next node itself.
                var element = reader.LocalName;
                var text = reader.ReadElementContentAsString().Trim();
                switch (element)
                {
                    case "pszAttributeName":
                        name = text;
                        break;
                    case "dwVersion":
                        version = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
                        break;
                    case "ftimeLastOriginatingChange":
                        time = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
                        break;
                    case "uuidLastOriginatingDsaInvocationID":
                        invocationId = Guid.TryParse(text, out var g) ? g : null;
                        break;
                    case "usnOriginatingChange":
                        usnOriginating = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) ? o : null;
                        break;
                    case "usnLocalChange":
                        usnLocal = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;
                        break;
                    case "pszLastOriginatingDsaDN":
                        dsaDn = text.Length == 0 ? null : text;
                        break;
                }
            }
        }
        catch (XmlException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(name) || version is null || time is null || invocationId is null || usnOriginating is null || usnLocal is null)
        {
            return false;
        }

        metadata = new AttributeMetadata(name, version.Value, time.Value, invocationId.Value, usnOriginating.Value, usnLocal.Value, dsaDn);
        return true;
    }

    [GeneratedRegex(@"&(?!(amp|lt|gt|quot|apos|#\d+|#x[0-9A-Fa-f]+);)", RegexOptions.CultureInvariant)]
    private static partial Regex UnescapedAmpersand();
}
