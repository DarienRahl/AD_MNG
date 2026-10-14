using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// <see cref="DirectoryObject"/> (read with Extended DN) → <see cref="SnapshotObject"/> with canonical values of the
/// watched attributes only (section 9). Unchanged attributes carry their stored <c>Version</c>/<c>ChangedAt</c> over;
/// changed ones get the detection time until replication metadata fills them (<see cref="ApplyMetadata"/>).
/// </summary>
internal sealed class SnapshotBuilder
{
    private readonly ValueCanonicalizer _canonicalizer;

    public SnapshotBuilder(ValueCanonicalizer canonicalizer)
    {
        _canonicalizer = canonicalizer;
    }

    /// <summary>Builds the new snapshot; null when the entry has no objectGUID.</summary>
    public SnapshotObject? Build(DirectoryObject entry, WatchObjectKind kind, IReadOnlySet<string> watched, SnapshotObject? previous, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(watched);
        if (entry.ObjectGuid is not { } guid)
        {
            return null;
        }

        var attributes = new Dictionary<string, SnapshotAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in watched)
        {
            if (!entry.TryGet(name, out var values) || _canonicalizer.Canonicalize(name, values) is not { } attribute)
            {
                continue;
            }

            if (previous is not null && previous.Attributes.TryGetValue(name, out var old) && old.Values.SequenceEqual(attribute.Values, StringComparer.Ordinal))
            {
                attribute = attribute with { Version = old.Version, ChangedAt = old.ChangedAt };
            }
            else
            {
                attribute = attribute with { ChangedAt = now };
            }

            attributes[name] = attribute;
        }

        attributes[WatchSignature.Key] = WatchSignature.Create(kind, watched);
        return new SnapshotObject
        {
            ObjectGuid = guid,
            Dn = entry.DistinguishedName,
            Kind = kind,
            ObjectClasses = entry.GetStrings(Attr.ObjectClass),
            Sid = entry.ObjectSid,
            SamAccountName = entry.GetString(Attr.SamAccountName),
            Name = entry.Dn.Name,
            DisplayName = entry.GetString(Attr.DisplayName),
            UsnCreated = entry.GetInt64(Attr.UsnCreated) ?? 0,
            UsnChanged = entry.GetInt64(Attr.UsnChanged) ?? 0,
            WhenCreated = entry.GetGeneralizedTime(Attr.WhenCreated),
            CreationPendingUntil = previous is { IsDeleted: false } ? previous.CreationPendingUntil : null,
            Attributes = attributes,
        };
    }

    /// <summary>Sets Version/ChangedAt of every attribute that has a metadata entry.</summary>
    public static SnapshotObject ApplyMetadata(SnapshotObject snapshot, ReplicationMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.IsEmpty)
        {
            return snapshot;
        }

        var attributes = new Dictionary<string, SnapshotAttribute>(snapshot.Attributes, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, attribute) in snapshot.Attributes)
        {
            if (metadata.Get(name) is { } entry)
            {
                attributes[name] = attribute with { Version = entry.Version, ChangedAt = entry.OriginatingTime };
            }
        }

        return snapshot with { Attributes = attributes };
    }
}
