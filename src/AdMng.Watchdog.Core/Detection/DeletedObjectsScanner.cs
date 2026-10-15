using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Show Deleted pass of the incremental cycle (section 10.9): one-level search of <c>CN=Deleted Objects</c> for
/// tombstones with <c>W' ≤ uSNChanged ≤ H</c>, restricted to the watched classes (children removed by tree-delete or
/// system churn – BitLocker, TPM, DNS nodes, devices, FSPs – never produce events). The query runs before the main pass
/// so the member differ knows which members were deleted; the events are produced afterwards. An unknown tombstone
/// (created and deleted between two polls) leaves a deleted marker row, so a repeated cycle, the later recycling or a
/// restore never report it as a new deletion; a recycled object whose row is gone (retention) is not reported again.
/// </summary>
internal sealed class DeletedObjectsScanner
{
    private static readonly string[] TombstoneAttributes =
    [
        Attr.ObjectGuid, Attr.ObjectSid, Attr.ObjectClass, Attr.SamAccountName, Attr.LastKnownParent, Attr.MsDsLastKnownRdn,
        Attr.IsRecycled, Attr.WhenChanged, Attr.UsnChanged, Attr.UsnCreated,
    ];

    /// <summary>The mangling of a deleted object's RDN: <c>&lt;name&gt;\0ADEL:&lt;objectGUID&gt;</c> (0x0A = line feed).</summary>
    private const string DeletedNameMarker = "\nDEL:";

    private readonly DetectionServices _services;

    public DeletedObjectsScanner(DetectionServices services)
    {
        _services = services;
    }

    /// <summary>
    /// The tombstone filter: isDeleted, <c>W' ≤ uSNChanged ≤ H</c>, watched classes only. Tombstones written after H was
    /// read belong to the next cycle (an unknown tombstone has no snapshot row that would make a second look harmless).
    /// </summary>
    public static string TombstoneFilter(long searchFrom, long highestUsn) => LdapFilter.And(
        LdapFilter.Equal(Attr.IsDeleted, "TRUE"),
        LdapFilter.GreaterOrEqual(Attr.UsnChanged, searchFrom),
        LdapFilter.LessOrEqual(Attr.UsnChanged, highestUsn),
        LdapFilter.Or(
            LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.User),
            LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Group),
            LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.OrganizationalUnit),
            LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Contact),
            LdapFilter.Equal(Attr.ObjectClass, WatchdogObjectClasses.GroupPolicyContainer),
            LdapFilter.Equal(Attr.ObjectClass, WatchdogObjectClasses.TrustedDomain),
            LdapFilter.Equal(Attr.ObjectClass, WatchdogObjectClasses.PasswordSettings)));

    /// <summary>Reads the tombstones of this cycle (empty when tracking is off or CN=Deleted Objects is unreadable).</summary>
    public async Task<IReadOnlyList<DirectoryObject>> ReadAsync(DetectionCycle cycle, CancellationToken cancellationToken)
    {
        if (!cycle.Settings.Directory.TrackDeletedObjects)
        {
            return [];
        }

        var result = new List<DirectoryObject>();
        try
        {
            await foreach (var tombstone in cycle.Session.SearchAsync(new DirectorySearch
            {
                BaseDn = DistinguishedName.Parse(cycle.Settings.Facts.DomainDn).Child("CN", "Deleted Objects").ToString(),
                Scope = SearchScope.OneLevel,
                Filter = TombstoneFilter(cycle.SearchFrom, cycle.Dc.HighestUsn),
                Attributes = TombstoneAttributes,
                IncludeDeleted = true,
                IncludeRecycled = cycle.Session.Info.RecycleBinEnabled,
                ExtendedDn = true,
                PageSize = 1000,
            }, cancellationToken).ConfigureAwait(false))
            {
                if (tombstone.ObjectGuid is { } guid)
                {
                    result.Add(tombstone);
                    cycle.DeletedGuids.Add(guid);
                }
            }
        }
        catch (DirectoryException ex) when (ex.Kind is DirectoryErrorKind.AccessDenied or DirectoryErrorKind.NoSuchObject or DirectoryErrorKind.UnsupportedControl)
        {
            cycle.Warnings.Add("deletedObjectsUnreadable");
            return [];
        }

        return result;
    }

    /// <summary>Produces the deletion events (10.9 table) into the cycle's current chunk.</summary>
    public async Task ProcessAsync(ObjectProcessor processor, DetectionCycle cycle, IReadOnlyList<DirectoryObject> tombstones, CancellationToken cancellationToken)
    {
        if (tombstones.Count == 0)
        {
            return;
        }

        var guids = tombstones.Select(t => t.ObjectGuid!.Value).ToList();
        var snapshots = await _services.Store.GetSnapshotsAsync(guids, cancellationToken).ConfigureAwait(false);
        foreach (var tombstone in tombstones)
        {
            var guid = tombstone.ObjectGuid!.Value;
            var deletedAt = tombstone.GetGeneralizedTime(Attr.WhenChanged) ?? cycle.Now;
            snapshots.TryGetValue(guid, out var snapshot);
            snapshot = cycle.Chunk.PendingUpsert(guid) ?? snapshot;
            if (snapshot is { IsDeleted: true })
            {
                continue;
            }

            // Recycle Bin: deleted object → recycled (deletedObjectLifetime later) is never a new deletion, also when the
            // deleted row is already gone (retention). Only an object created after the last poll and deleted and
            // purged before this one is new. Without the Recycle Bin every tombstone is isRecycled = TRUE.
            if (snapshot is null && cycle.Session.Info.RecycleBinEnabled && tombstone.GetBoolean(Attr.IsRecycled) == true
                && (tombstone.GetInt64(Attr.UsnCreated) ?? 0) < cycle.SearchFrom)
            {
                continue;
            }

            if (snapshot is not null)
            {
                await DeleteKnownAsync(processor, cycle, snapshot, deletedAt, DetectionSource.DeletedObjects, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await DeleteUnknownAsync(processor, cycle, tombstone, deletedAt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ObjectDeleted for a live snapshot row (the deferred Created event first when it was still settling), with the
    /// former groups as memberOf context, and the Deletions entry (the store removes the key from every member list).
    /// </summary>
    public async Task DeleteKnownAsync(ObjectProcessor processor, DetectionCycle cycle, SnapshotObject snapshot, DateTimeOffset deletedAt,
        DetectionSource source, CancellationToken cancellationToken)
    {
        if (snapshot.CreationPendingUntil is not null)
        {
            await processor.EmitCreatedAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        var key = snapshot.ObjectGuid.ToString("N");
        var groups = await _services.Store.GetGroupsContainingAsync(key, cancellationToken).ConfigureAwait(false);
        var groupDns = groups.Count == 0
            ? []
            : (await _services.Store.GetSnapshotsAsync(groups, cancellationToken).ConfigureAwait(false)).Values.Select(g => g.Dn).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var changes = groupDns.Count == 0 ? [] : new[] { processor.Changes.Text(Attr.MemberOf, [], groupDns) with { ValueKind = AttributeValueKind.Dn } };
        var draft = new EventDraft
        {
            Kind = WatchEventKind.ObjectDeleted,
            PreviousDn = snapshot.Dn,
            Changes = changes,
            MetadataAttributes = [Attr.IsDeleted],
        };
        processor.Emit([draft], snapshot, snapshot.ToRef(), ReplicationMetadata.Empty, deletedAt, source);
        cycle.Chunk.Delete(snapshot.ObjectGuid, deletedAt);
        cycle.DeletedGuids.Add(snapshot.ObjectGuid);
        if (WatchObjectKinds.IsPrincipal(snapshot.Kind))
        {
            cycle.DeletedPrincipalKeys.Add(key);
            cycle.Chunk.MembershipRelevant = true;
            cycle.PrivilegeRelevant = true;
        }
    }

    /// <summary>
    /// A tombstone of an object created and deleted between two polls: reported only when its lastKnownParent resolves
    /// to an in-scope location (through the snapshot or the parent tombstones' chain), never below a deleted user or
    /// computer (system children).
    /// </summary>
    private async Task DeleteUnknownAsync(ObjectProcessor processor, DetectionCycle cycle, DirectoryObject tombstone, DateTimeOffset deletedAt, CancellationToken cancellationToken)
    {
        if (WatchObjectKinds.FromObjectClasses(tombstone.GetStrings(Attr.ObjectClass)) is not { } kind)
        {
            return;
        }

        var parent = await ResolveParentAsync(cycle, tombstone.GetString(Attr.LastKnownParent), 0, cancellationToken).ConfigureAwait(false);
        var rdn = LastKnownRdn(tombstone);
        if (parent is null || rdn is null || !DistinguishedName.TryParse(parent, out var parentDn))
        {
            return;
        }

        var dn = parentDn.Child(DistinguishedName.Parse(tombstone.DistinguishedName).Leaf.Type, rdn).ToString();
        if (!cycle.Scope.IsInScope(dn, kind))
        {
            return;
        }

        var reference = new ObjectRef
        {
            ObjectGuid = tombstone.ObjectGuid,
            Dn = dn,
            Name = rdn,
            SamAccountName = tombstone.GetString(Attr.SamAccountName),
            Sid = tombstone.ObjectSid,
            Kind = kind,
        };
        var draft = new EventDraft { Kind = WatchEventKind.ObjectDeleted, PreviousDn = dn, MetadataAttributes = [Attr.IsDeleted] };
        processor.Emit([draft], null, reference, ReplicationMetadata.Empty, deletedAt, DetectionSource.DeletedObjects);

        // The deleted marker row (no attributes) in the same chunk as the event: a repeated cycle, the later recycling
        // and a restore (ObjectRestored) then know the object.
        cycle.Chunk.UpsertDeleted(new SnapshotObject
        {
            ObjectGuid = tombstone.ObjectGuid!.Value,
            Dn = dn,
            Kind = kind,
            ObjectClasses = tombstone.GetStrings(Attr.ObjectClass),
            Sid = tombstone.ObjectSid,
            SamAccountName = reference.SamAccountName,
            Name = rdn,
            UsnCreated = tombstone.GetInt64(Attr.UsnCreated) ?? 0,
            UsnChanged = tombstone.GetInt64(Attr.UsnChanged) ?? 0,
            IsDeleted = true,
            DeletedAt = deletedAt,
        });
        if (WatchObjectKinds.IsPrincipal(kind))
        {
            cycle.Chunk.MembershipRelevant = true;
            cycle.PrivilegeRelevant = true;
        }
    }

    /// <summary>The last live DN of a lastKnownParent value; null when it is out of reach or a deleted user/computer.</summary>
    private async Task<string?> ResolveParentAsync(DetectionCycle cycle, string? lastKnownParent, int depth, CancellationToken cancellationToken)
    {
        if (lastKnownParent is null || depth > 10 || !ExtendedDistinguishedName.TryParse(lastKnownParent, out var parent))
        {
            return null;
        }

        if (parent.Guid is not { } guid)
        {
            return DeletedObjectsDn(parent.Dn) ? null : parent.Dn;
        }

        var snapshots = await _services.Store.GetSnapshotsAsync([guid], cancellationToken).ConfigureAwait(false);
        var snapshot = cycle.Chunk.PendingUpsert(guid) ?? (snapshots.TryGetValue(guid, out var stored) ? stored : null);
        if (snapshot is not null)
        {
            return snapshot.Kind is WatchObjectKind.User or WatchObjectKind.Computer or WatchObjectKind.ServiceAccount ? null : snapshot.Dn;
        }

        if (!DeletedObjectsDn(parent.Dn))
        {
            return parent.Dn;
        }

        await foreach (var parentTombstone in cycle.Session.SearchAsync(new DirectorySearch
        {
            BaseDn = DistinguishedName.Parse(cycle.Settings.Facts.DomainDn).Child("CN", "Deleted Objects").ToString(),
            Scope = SearchScope.OneLevel,
            Filter = LdapFilter.EqualGuid(Attr.ObjectGuid, guid),
            Attributes = [Attr.ObjectClass, Attr.LastKnownParent, Attr.MsDsLastKnownRdn],
            IncludeDeleted = true,
            IncludeRecycled = cycle.Session.Info.RecycleBinEnabled,
            ExtendedDn = true,
        }, cancellationToken).ConfigureAwait(false))
        {
            if (WatchObjectKinds.FromObjectClasses(parentTombstone.GetStrings(Attr.ObjectClass)) is WatchObjectKind.User or WatchObjectKind.Computer or WatchObjectKind.ServiceAccount)
            {
                return null;
            }

            var grandParent = await ResolveParentAsync(cycle, parentTombstone.GetString(Attr.LastKnownParent), depth + 1, cancellationToken).ConfigureAwait(false);
            var rdn = LastKnownRdn(parentTombstone);
            return grandParent is null || rdn is null
                ? null
                : DistinguishedName.Parse(grandParent).Child(DistinguishedName.Parse(parentTombstone.DistinguishedName).Leaf.Type, rdn).ToString();
        }

        return null;
    }

    /// <summary>
    /// The RDN value an object had before it was deleted: <c>msDS-LastKnownRDN</c> (set on the Recycle Bin path only),
    /// else the tombstone's leaf value cut at the <c>\0ADEL:&lt;guid&gt;</c> mangling (tombstones without the Recycle
    /// Bin – Samba, and Windows unless the feature is enabled – lab-verified); null when neither is available.
    /// </summary>
    public static string? LastKnownRdn(DirectoryObject tombstone)
    {
        ArgumentNullException.ThrowIfNull(tombstone);
        return tombstone.GetString(Attr.MsDsLastKnownRdn) ?? UnmangledName(tombstone.DistinguishedName);
    }

    /// <summary>The leaf value of a deleted object's DN without the <c>\0ADEL:&lt;guid&gt;</c> suffix; null when not mangled.</summary>
    public static string? UnmangledName(string tombstoneDn)
    {
        if (!DistinguishedName.TryParse(tombstoneDn, out var dn) || dn.IsRoot)
        {
            return null;
        }

        var marker = dn.Leaf.Value.IndexOf(DeletedNameMarker, StringComparison.Ordinal);
        return marker > 0 ? dn.Leaf.Value[..marker] : null;
    }

    /// <summary>A deleted marker row of an unknown tombstone (no watched attributes, unlike every built snapshot).</summary>
    public static bool IsDeletedMarker(SnapshotObject snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.IsDeleted && snapshot.Attributes.Count == 0;
    }

    private static bool DeletedObjectsDn(string dn) =>
        DistinguishedName.TryParse(dn, out var parsed) && parsed.Rdns.Any(r => string.Equals(r.Value, "Deleted Objects", StringComparison.OrdinalIgnoreCase));
}
