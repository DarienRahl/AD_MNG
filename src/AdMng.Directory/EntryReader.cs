using System.DirectoryServices.Protocols;
using System.Text;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Directory;

/// <summary>Converts S.DS.P search entries to <see cref="DirectoryObject"/> using the schema for value types.</summary>
internal static class EntryReader
{
    /// <summary>Builds an in-memory snapshot of one search result entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="schema">Schema for value types.</param>
    /// <param name="extendedDn">The request carried the Extended DN control: AD (and Samba) then return the entry DN
    /// itself in extended form (<c>&lt;GUID=…&gt;;&lt;SID=…&gt;;dn</c>) – it is stripped to the plain DN, attribute values
    /// keep the extended form.</param>
    public static DirectoryObject ToDirectoryObject(SearchResultEntry entry, ISchemaCatalog schema, bool extendedDn = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(schema);
        var attributes = new List<AttributeValues>(entry.Attributes.Count);
        foreach (DirectoryAttribute attribute in entry.Attributes.Values)
        {
            attributes.Add(Decode(attribute.Name, attribute.GetValues(typeof(byte[])), schema));
        }

        var dn = extendedDn ? PlainDn(entry.DistinguishedName) : entry.DistinguishedName;
        return new DirectoryObject(dn, attributes);
    }

    /// <summary>The plain DN of a value that may be in extended form; other values are returned unchanged.</summary>
    public static string PlainDn(string distinguishedName) =>
        distinguishedName.StartsWith('<') && ExtendedDistinguishedName.TryParse(distinguishedName, out var parsed)
            ? parsed.Dn
            : distinguishedName;

    /// <summary>
    /// Decodes raw values: binary syntaxes (per schema, also for ranged descriptions) stay byte[],
    /// everything else is UTF-8 text.
    /// </summary>
    public static AttributeValues Decode(string name, IReadOnlyList<object> rawValues, ISchemaCatalog schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(rawValues);
        ArgumentNullException.ThrowIfNull(schema);
        var binary = schema.IsBinary(name);
        var values = new object[rawValues.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var bytes = rawValues[i] as byte[] ?? Encoding.UTF8.GetBytes(rawValues[i]?.ToString() ?? string.Empty);
            values[i] = binary ? bytes : Encoding.UTF8.GetString(bytes);
        }

        return new AttributeValues(name, values);
    }

    /// <summary>Raw values of one attribute as UTF-8 strings (used for ranged retrieval).</summary>
    public static IEnumerable<string> Strings(DirectoryAttribute attribute)
    {
        foreach (var value in attribute.GetValues(typeof(byte[])))
        {
            yield return Encoding.UTF8.GetString((byte[])value);
        }
    }
}
