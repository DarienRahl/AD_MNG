using System.Buffers.Binary;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Decoder of the binary <c>replPropertyMetaData</c> attribute (Samba, or Windows when the constructed XML attribute is
/// unreadable – section 10.10). Little-endian: header <c>uint version (=1), uint reserved, uint count, uint reserved</c>
/// (16 bytes), then <c>count</c> entries of 48 bytes: <c>uint attrTyp, uint version, long timeChanged (seconds since
/// 1601-01-01 UTC), Guid originatingInvocationId, long usnOriginating, long usnLocal</c>. Blobs with a wrong length or
/// version are rejected (warning, no metadata).
/// </summary>
public static class ReplicationMetadataDecoder
{
    private const int HeaderLength = 16;
    private const int EntryLength = 48;

    private static readonly DateTimeOffset Epoch1601 = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>One raw entry of the blob (ATTRTYP not yet resolved).</summary>
    public readonly record struct RawEntry(uint AttrTyp, int Version, DateTimeOffset OriginatingTime, Guid InvocationId, long UsnOriginating, long UsnLocal);

    /// <summary>Decodes the raw entries; false for a malformed blob.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> blob, out IReadOnlyList<RawEntry> entries)
    {
        entries = [];
        if (blob.Length < HeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(blob) != 1)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]);
        if (count > (blob.Length - HeaderLength) / EntryLength || blob.Length != HeaderLength + ((int)count * EntryLength))
        {
            return false;
        }

        var result = new List<RawEntry>((int)count);
        for (var i = 0; i < count; i++)
        {
            var entry = blob.Slice(HeaderLength + (i * EntryLength), EntryLength);
            var seconds = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]);
            if (seconds < 0 || seconds > 315_537_897_599L)
            {
                return false;
            }

            result.Add(new RawEntry(
                BinaryPrimitives.ReadUInt32LittleEndian(entry),
                unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..])),
                Epoch1601.AddSeconds(seconds),
                new Guid(entry.Slice(16, 16)),
                BinaryPrimitives.ReadInt64LittleEndian(entry[32..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[40..])));
        }

        entries = result;
        return true;
    }

    /// <summary>Decodes and resolves attribute names; unresolvable ATTRTYPs are ignored.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> blob, AttrTypResolver resolver, out ReplicationMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        metadata = ReplicationMetadata.Empty;
        if (!TryDecode(blob, out var entries))
        {
            return false;
        }

        metadata = new ReplicationMetadata(entries
            .Select(e => resolver.Resolve(e.AttrTyp) is { } name
                ? new AttributeMetadata(name, e.Version, e.OriginatingTime, e.InvocationId, e.UsnOriginating, e.UsnLocal)
                : null)
            .OfType<AttributeMetadata>());
        return true;
    }
}
