using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using AdMng.Core.Ldap;

namespace AdMng.Core.ActiveDirectory;

/// <summary>
/// One value of the constructed attribute <c>msDS-ReplAttributeMetaData</c>: who wrote an attribute last (the originating
/// DC's NTDS Settings object), when and in which version. Directory metadata only, kept in memory.
/// </summary>
/// <param name="AttributeName">The attribute (<c>pszAttributeName</c>).</param>
/// <param name="Version">The attribute's version (<c>dwVersion</c>).</param>
/// <param name="LastOriginatingChange">When the last originating write happened (<c>ftimeLastOriginatingChange</c>, UTC).</param>
/// <param name="OriginatingDsaDn">The NTDS Settings object of the DC that made the write (<c>pszLastOriginatingDsaDN</c>), null
/// when the value does not name it (the DC was demoted, or the attribute was never written).</param>
public sealed record AttributeReplicationEntry(string AttributeName, int Version, DateTimeOffset LastOriginatingChange, string? OriginatingDsaDn)
{
    /// <summary>The DN of the originating DC's server object (the parent of its NTDS Settings object), null when not known.</summary>
    public string? OriginatingServerDn =>
        OriginatingDsaDn is { } dsa && DistinguishedName.TryParse(dsa, out var dn) && dn.Parent is { IsRoot: false } server ? server.ToString() : null;

    /// <summary>The attribute name only.</summary>
    public override string ToString() => AttributeName;
}

/// <summary>
/// Parser of <c>msDS-ReplAttributeMetaData</c> (one <c>&lt;DS_REPL_ATTR_META_DATA&gt;</c> XML document per attribute), for
/// "which DC wrote lockoutTime" of the lockout source (design note e-event-logs.md 4.1). Windows may append a NUL character
/// and leaves <c>&amp;</c> in DNs unescaped – both are repaired before parsing with a hardened <see cref="XmlReader"/> (no
/// DTD, no resolver, size limit). Never throws for malformed values (the Watchdog service has its own parser of the same
/// format with the invocation IDs and USNs it needs).
/// </summary>
public static partial class AttributeReplicationMetadata
{
    /// <summary>The constructed attribute (read it explicitly; it is not returned for <c>*</c>).</summary>
    public const string AttributeName = "msDS-ReplAttributeMetaData";

    /// <summary>The longest value accepted.</summary>
    public const int MaxValueLength = 64 * 1024;

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = MaxValueLength,
    };

    /// <summary>Parses one value; false (and null) for an empty, oversized or malformed value or one without a name, version or time.</summary>
    public static bool TryParse(string? value, out AttributeReplicationEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxValueLength)
        {
            return false;
        }

        var sanitized = UnescapedAmpersand().Replace(value.Replace("\0", string.Empty, StringComparison.Ordinal), "&amp;");
        string? name = null, dsa = null;
        int? version = null;
        DateTimeOffset? time = null;
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
                        time = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
                            ? t
                            : null;
                        break;
                    case "pszLastOriginatingDsaDN":
                        dsa = text.Length == 0 ? null : text;
                        break;
                }
            }
        }
        catch (XmlException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(name) || version is null || time is null)
        {
            return false;
        }

        entry = new AttributeReplicationEntry(name, version.Value, time.Value, dsa);
        return true;
    }

    /// <summary>The entry of <paramref name="attribute"/> (case-insensitive) among <paramref name="values"/>, null when none parses.</summary>
    public static AttributeReplicationEntry? Find(IEnumerable<string> values, string attribute)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        foreach (var value in values)
        {
            if (TryParse(value, out var entry) && string.Equals(entry!.AttributeName, attribute, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    [GeneratedRegex(@"&(?!(amp|lt|gt|quot|apos|#\d+|#x[0-9A-Fa-f]+);)", RegexOptions.CultureInvariant)]
    private static partial Regex UnescapedAmpersand();
}
