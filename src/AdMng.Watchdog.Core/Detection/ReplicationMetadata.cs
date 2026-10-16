namespace AdMng.Watchdog.Core.Detection;

/// <summary>Replication metadata of one attribute (DS_REPL_ATTR_META_DATA / PROPERTY_META_DATA_EXT).</summary>
/// <param name="Attribute">LDAP display name.</param>
/// <param name="Version">dwVersion – incremented by every originating write.</param>
/// <param name="OriginatingTime">Time of the last originating write (UTC).</param>
/// <param name="OriginatingInvocationId">invocationId of the DC that made the write.</param>
/// <param name="UsnOriginating">USN of the write on the originating DC.</param>
/// <param name="UsnLocal">USN of the write on the DC that was read (compare with the watermark).</param>
/// <param name="OriginatingDsaDn">NTDS Settings DN of the originating DC (Windows XML only).</param>
public sealed record AttributeMetadata(
    string Attribute,
    int Version,
    DateTimeOffset OriginatingTime,
    Guid OriginatingInvocationId,
    long UsnOriginating,
    long UsnLocal,
    string? OriginatingDsaDn = null);

/// <summary>The replication metadata of one object: lookup by attribute name (case-insensitive).</summary>
public sealed class ReplicationMetadata
{
    private readonly Dictionary<string, AttributeMetadata> _entries;

    public ReplicationMetadata(IEnumerable<AttributeMetadata> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = new Dictionary<string, AttributeMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            _entries[entry.Attribute] = entry;
        }
    }

    /// <summary>No metadata available (fallback windows in attribution).</summary>
    public static ReplicationMetadata Empty { get; } = new([]);

    public bool IsEmpty => _entries.Count == 0;

    public IReadOnlyCollection<AttributeMetadata> Entries => _entries.Values;

    /// <summary>The entry of an attribute, or null.</summary>
    public AttributeMetadata? Get(string attribute) => _entries.TryGetValue(attribute, out var entry) ? entry : null;

    /// <summary>The newest entry (by originating time) among <paramref name="attributes"/> whose usnLocal is above
    /// <paramref name="watermark"/> (section 10.10: OriginatingTime/OriginatingDc of an event); null when none.</summary>
    public AttributeMetadata? NewestAbove(IEnumerable<string> attributes, long watermark)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        return attributes
            .Select(Get)
            .Where(e => e is not null && e.UsnLocal > watermark)
            .MaxBy(e => e!.OriginatingTime);
    }
}
