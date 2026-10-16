using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Disappeared handling of a full scan (section 10.9): every live snapshot row under the default NC that the scan did
/// not return is looked up by objectGUID with Show Deleted – deleted → ObjectDeleted (Deletions), live outside scope →
/// ObjectMoved outOfScope (Removals), not found → ObjectDisappeared (Removals; a later re-appearance is ObjectAppeared,
/// never ObjectRestored). Configuration-NC rows (DCs, PKI, forest singletons) are reconciled by the
/// ConfigurationPartitionWatcher.
/// </summary>
internal sealed class Reconciler
{
    private const int BatchSize = 500;

    private readonly DetectionServices _services;
    private readonly DeletedObjectsScanner _deletions;

    public Reconciler(DetectionServices services, DeletedObjectsScanner deletions)
    {
        _services = services;
        _deletions = deletions;
    }

    /// <summary>Handles the unseen rows; commits in chunks.</summary>
    public async Task ReconcileAsync(ObjectProcessor processor, DetectionCycle cycle, CancellationToken cancellationToken)
    {
        var missing = new List<Guid>();
        await foreach (var guid in _services.Store.GetLiveObjectGuidsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!cycle.Seen.Contains(guid))
            {
                missing.Add(guid);
            }
        }

        foreach (var batch in missing.Chunk(BatchSize))
        {
            var snapshots = await _services.Store.GetSnapshotsAsync(batch, cancellationToken).ConfigureAwait(false);
            foreach (var snapshot in snapshots.Values)
            {
                if (snapshot.IsDeleted || !cycle.Scope.IsInDomainPartition(snapshot.Dn)
                    || snapshot.Kind is WatchObjectKind.DomainController or WatchObjectKind.PublicKeyInfrastructure)
                {
                    continue;
                }

                await ReconcileOneAsync(processor, cycle, snapshot, cancellationToken).ConfigureAwait(false);
            }

            await cycle.CommitChunkAsync(_services.Store, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileOneAsync(ObjectProcessor processor, DetectionCycle cycle, SnapshotObject snapshot, CancellationToken cancellationToken)
    {
        var found = await FindAsync(cycle, snapshot.ObjectGuid, cancellationToken).ConfigureAwait(false);
        if (found is not null && found.GetBoolean(Attr.IsDeleted) == true)
        {
            var deletedAt = found.GetGeneralizedTime(Attr.WhenChanged) ?? cycle.Now;
            await _deletions.DeleteKnownAsync(processor, cycle, snapshot, deletedAt, DetectionSource.Reconciliation, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (found is not null)
        {
            var moved = snapshot with { Dn = found.DistinguishedName };
            var draft = new EventDraft
            {
                Kind = WatchEventKind.ObjectMoved,
                PreviousDn = snapshot.Dn,
                ExtraTags = [EventTags.OutOfScope],
                MetadataAttributes = [Attr.Name],
            };
            processor.Emit([draft], moved, moved.ToRef(), ReplicationMetadata.Empty, found.GetGeneralizedTime(Attr.WhenChanged), DetectionSource.Reconciliation);
        }
        else
        {
            var draft = new EventDraft { Kind = WatchEventKind.ObjectDisappeared, PreviousDn = snapshot.Dn };
            processor.Emit([draft], snapshot, snapshot.ToRef(), ReplicationMetadata.Empty, null, DetectionSource.Reconciliation);
        }

        cycle.Chunk.Remove(snapshot.ObjectGuid);
        if (WatchObjectKinds.IsPrincipal(snapshot.Kind))
        {
            if (found is null)
            {
                cycle.DeletedPrincipalKeys.Add(snapshot.ObjectGuid.ToString("N"));
            }

            cycle.Chunk.MembershipRelevant = true;
            cycle.PrivilegeRelevant = true;
        }
    }

    private static async Task<DirectoryObject?> FindAsync(DetectionCycle cycle, Guid guid, CancellationToken cancellationToken)
    {
        await foreach (var entry in cycle.Session.SearchAsync(new DirectorySearch
        {
            BaseDn = cycle.Settings.Facts.DomainDn,
            Filter = LdapFilter.EqualGuid(Attr.ObjectGuid, guid),
            Attributes = [Attr.ObjectGuid, Attr.IsDeleted, Attr.WhenChanged],
            IncludeDeleted = true,
            IncludeRecycled = cycle.Session.Info.RecycleBinEnabled,
            SizeLimit = 1,
        }, cancellationToken).ConfigureAwait(false))
        {
            return entry;
        }

        return null;
    }
}
