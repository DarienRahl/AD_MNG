using AdMng.Core.Directory;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// One base read per object (section 10.10): <c>msDS-ReplAttributeMetaData</c> (Windows XML) preferred, the binary
/// <c>replPropertyMetaData</c> (Samba, or Windows when the constructed attribute is unreadable) as fallback; neither →
/// no metadata (attribution uses its fallback windows). The ATTRTYP resolver is loaded once per session.
/// </summary>
internal sealed class ReplicationMetadataReader
{
    private static readonly string[] Attributes = [WatchdogAttributes.ReplAttributeMetaData, WatchdogAttributes.ReplPropertyMetaData];

    private readonly Lock _gate = new();
    private (IDirectorySession Session, Task<AttrTypResolver> Resolver)? _resolver;

    /// <summary>Number of unparsable XML values or blobs since start (warning counter, no values).</summary>
    public int Rejected { get; private set; }

    /// <summary>The metadata of <paramref name="dn"/>; empty when unavailable.</summary>
    public async Task<ReplicationMetadata> ReadAsync(IDirectorySession session, string dn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(dn);
        AdMng.Core.Model.DirectoryObject? entry;
        try
        {
            entry = await session.GetAsync(dn, Attributes, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex) when (!ex.IsTransient)
        {
            return ReplicationMetadata.Empty;
        }

        if (entry is null)
        {
            return ReplicationMetadata.Empty;
        }

        var xml = entry.GetStrings(WatchdogAttributes.ReplAttributeMetaData);
        if (xml.Count > 0)
        {
            var parsed = ReplicationMetadataXmlParser.Parse(xml, out var rejected);
            Rejected += rejected;
            if (!parsed.IsEmpty)
            {
                return parsed;
            }
        }

        if (entry.GetBytes(WatchdogAttributes.ReplPropertyMetaData) is { Length: > 0 } blob)
        {
            var resolver = await GetResolverAsync(session, cancellationToken).ConfigureAwait(false);
            if (ReplicationMetadataDecoder.TryDecode(blob, resolver, out var metadata))
            {
                return metadata;
            }

            Rejected++;
        }

        return ReplicationMetadata.Empty;
    }

    private Task<AttrTypResolver> GetResolverAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_resolver is { } cached && ReferenceEquals(cached.Session, session) && !cached.Resolver.IsFaulted && !cached.Resolver.IsCanceled)
            {
                return cached.Resolver;
            }

            var task = AttrTypResolver.LoadAsync(session, cancellationToken);
            _resolver = (session, task);
            return task;
        }
    }
}
