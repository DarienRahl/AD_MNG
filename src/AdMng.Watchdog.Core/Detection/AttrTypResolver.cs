using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Schema;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// ATTRTYP → lDAPDisplayName for binary <c>replPropertyMetaData</c> (section 10.10): MS-DRSR 5.16.4
/// <c>OidFromAttid</c> with the default prefix table (verified against Samba <c>schema_prefixmap.c</c>), the session
/// schema's attributeID (OID) → name, plus <c>msDS-IntId</c> values for attributes added to the schema after Windows
/// 2003 (their ATTRTYP is the msDS-IntId itself). Unresolvable ATTRTYPs yield null.
/// </summary>
public sealed class AttrTypResolver
{
    /// <summary>MS-DRSR default prefix table: index → OID prefix.</summary>
    private static readonly string[] DefaultPrefixes =
    [
        "2.5.4", "2.5.6", "1.2.840.113556.1.2", "1.2.840.113556.1.3", "2.16.840.1.101.2.2.1", "2.16.840.1.101.2.2.3",
        "2.16.840.1.101.2.1.5", "2.16.840.1.101.2.1.4", "2.5.5", "1.2.840.113556.1.4", "1.2.840.113556.1.5",
        "1.2.840.113556.1.4.260", "1.2.840.113556.1.5.56", "1.2.840.113556.1.4.262", "1.2.840.113556.1.5.57",
        "1.2.840.113556.1.4.263", "1.2.840.113556.1.5.58", "1.2.840.113556.1.5.73", "1.2.840.113556.1.4.305",
        "0.9.2342.19200300.100", "2.16.840.1.113730.3", "0.9.2342.19200300.100.1", "2.16.840.1.113730.3.1",
        "1.2.840.113556.1.5.7000", "2.5.21", "2.5.18", "2.5.20", "1.3.6.1.4.1.1466.101.119", "2.16.840.1.113730.3.2",
        "1.3.6.1.4.1.250.1", "1.2.840.113549.1.9", "0.9.2342.19200300.100.4", "1.2.840.113556.1.6.23",
        "1.2.840.113556.1.6.18.1", "1.2.840.113556.1.6.18.2", "1.2.840.113556.1.6.13.3", "1.2.840.113556.1.6.13.4",
        "1.3.6.1.1.1.1", "1.3.6.1.1.1.2",
    ];

    private readonly Dictionary<string, string> _namesByOid;
    private readonly IReadOnlyDictionary<uint, string> _namesByIntId;

    /// <param name="schema">Session schema (attributeID → lDAPDisplayName).</param>
    /// <param name="intIds">msDS-IntId → lDAPDisplayName (optional).</param>
    public AttrTypResolver(ISchemaCatalog schema, IReadOnlyDictionary<uint, string>? intIds = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _namesByOid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in schema.Attributes)
        {
            if (!string.IsNullOrEmpty(attribute.AttributeId))
            {
                _namesByOid[attribute.AttributeId] = attribute.LdapName;
            }
        }

        _namesByIntId = intIds ?? new Dictionary<uint, string>();
    }

    /// <summary>
    /// Loads the msDS-IntId map from the schema NC (<c>(&amp;(objectClass=attributeSchema)(msDS-IntId=*))</c>, one
    /// paged search) and combines it with the session schema. A failed query leaves only the prefix-table mapping.
    /// </summary>
    public static async Task<AttrTypResolver> LoadAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var intIds = new Dictionary<uint, string>();
        var search = new DirectorySearch
        {
            BaseDn = session.Info.SchemaNamingContext,
            Scope = SearchScope.OneLevel,
            Filter = LdapFilter.And(LdapFilter.Equal("objectClass", "attributeSchema"), LdapFilter.Present(WatchdogAttributes.IntId)),
            Attributes = [WatchdogAttributes.IntId, WatchdogAttributes.LdapDisplayName],
            PageSize = 1000,
        };
        try
        {
            await foreach (var entry in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
            {
                if (entry.GetInt64(WatchdogAttributes.IntId) is { } id && entry.GetString(WatchdogAttributes.LdapDisplayName) is { } name)
                {
                    intIds[unchecked((uint)id)] = name;
                }
            }
        }
        catch (DirectoryException)
        {
            // Without msDS-IntId only the prefix-table attributes resolve (all base-schema attributes do).
        }

        return new AttrTypResolver(session.Schema, intIds);
    }

    /// <summary>lDAPDisplayName of an ATTRTYP, or null.</summary>
    public string? Resolve(uint attrTyp)
    {
        if (_namesByIntId.TryGetValue(attrTyp, out var intIdName))
        {
            return intIdName;
        }

        return OidFromAttid(attrTyp) is { } oid && _namesByOid.TryGetValue(oid, out var name) ? name : null;
    }

    /// <summary>MS-DRSR <c>OidFromAttid</c> with the default prefix table; null for an unknown prefix index.</summary>
    public static string? OidFromAttid(uint attrTyp)
    {
        var index = attrTyp >> 16;
        if (index >= DefaultPrefixes.Length)
        {
            return null;
        }

        // The lower word is the last arc; values ≥ 128 are BER-encoded in two bytes whose high bit (0x8000) may be set.
        var lastArc = attrTyp & 0x7FFF;
        return string.Create(CultureInfo.InvariantCulture, $"{DefaultPrefixes[index]}.{lastArc}");
    }

    /// <summary>The ATTRTYP of an OID under the default prefix table (last arc &lt; 16384), or null.</summary>
    public static uint? AttidFromOid(string oid)
    {
        ArgumentNullException.ThrowIfNull(oid);
        var lastDot = oid.LastIndexOf('.');
        if (lastDot <= 0 || !uint.TryParse(oid.AsSpan(lastDot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var lastArc)
            || lastArc >= 16384)
        {
            return null;
        }

        var index = Array.IndexOf(DefaultPrefixes, oid[..lastDot]);
        return index < 0 ? null : ((uint)index << 16) | lastArc;
    }
}
