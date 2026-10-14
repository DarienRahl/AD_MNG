using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Show Deleted pass of the incremental cycle (section 10.9): one-level search of <c>CN=Deleted Objects</c> for
/// tombstones with <c>uSNChanged ≥ W'</c>, restricted to the watched classes (children removed by tree-delete or system
/// churn – BitLocker, TPM, DNS nodes, devices, FSPs – never produce events). The query runs before the main pass so the
/// member differ knows which members were deleted; the events are produced afterwards.
/// </summary>
internal sealed class DeletedObjectsScanner
{
    private static readonly string[] TombstoneAttributes =
    [
        Attr.ObjectGuid, Attr.ObjectSid, Attr.ObjectClass, Attr.SamAccountName, Attr.LastKnownParent, Attr.MsDsLastKnownRdn,
        Attr.IsRecycled, Attr.WhenChanged, Attr.UsnChanged,
    ];

    private readonly DetectionServices _services;

    public DeletedObjectsScanner(DetectionServices services)
    {
        _services = services;
    }

    /// <summary>The tombstone filter: isDeleted, uSNChanged ≥ W', watched classes only.</summary>
    public static string TombstoneFilter(long searchFrom) => LdapFilter.And(
        LdapFilter.Equal(Attr.IsDeleted, "TRUE"),
        LdapFilter.GreaterOrEqual(Attr.UsnChanged, searchFrom),
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
                Filter = TombstoneFilter(cycle.SearchFrom),
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
        var rdn = tombstone.GetString(Attr.MsDsLastKnownRdn);
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
        cycle.Chunk.Delete(tombstone.ObjectGuid!.Value, deletedAt);
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
            var rdn = parentTombstone.GetString(Attr.MsDsLastKnownRdn);
            return grandParent is null || rdn is null
                ? null
                : DistinguishedName.Parse(grandParent).Child(DistinguishedName.Parse(parentTombstone.DistinguishedName).Leaf.Type, rdn).ToString();
        }

        return null;
    }

    private static bool DeletedObjectsDn(string dn) =>
        DistinguishedName.TryParse(dn, out var parsed) && parsed.Rdns.Any(r => string.Equals(r.Value, "Deleted Objects", StringComparison.OrdinalIgnoreCase));
}
