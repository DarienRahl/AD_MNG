using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Per-object processing of section 10.6: SnapshotBuilder → ObjectDiffer → ChangeClassifier → EventTagger →
/// SeverityResolver, including scope changes, new objects (creation settle window), appeared objects, restores
/// (memberships re-inserted, 10.9), group membership (10.8), replication metadata (10.10) and the version-aware
/// stale-DC check of Resync/Failback (10.4). Writes go into the cycle's current chunk.
/// </summary>
internal sealed class ObjectProcessor
{
    private static readonly TimeSpan LongDisabledAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan LastLogonLag = TimeSpan.FromDays(44);

    private readonly DetectionServices _services;
    private readonly DetectionCycle _cycle;
    private readonly SnapshotBuilder _builder;
    private readonly TaggingContext _tagging;
    private readonly Dictionary<Guid, bool> _exists = [];
    private bool _dcDirectoryRefreshed;

    public ObjectProcessor(DetectionServices services, DetectionCycle cycle)
    {
        _services = services;
        _cycle = cycle;
        var configuration = cycle.Settings.Configuration;
        Canonicalizer = new ValueCanonicalizer(cycle.Session.Schema);
        _builder = new SnapshotBuilder(Canonicalizer);
        Changes = new AttributeChangeBuilder(configuration.Notifications.MaxValueLength, configuration.Directory.MaxValuesPerAttribute, cycle.Session.Schema);
        Classifier = new ChangeClassifier(Changes);
        Factory = new EventFactory(services.Time, services.Severity);
        _tagging = new TaggingContext
        {
            IsPrivileged = cycle.IsPrivileged,
            IsPrivilegedRoot = s => s.Kind == WatchObjectKind.Group && cycle.Settings.Privileged.TierOf(s.Sid, s.SamAccountName, s.Dn) is not null,
            Containers = cycle.Dc.Containers,
            ServiceAccountSubtrees = cycle.Settings.ServiceAccountSubtrees,
            DisabledAccountsSubtrees = cycle.Settings.DisabledAccountsSubtrees,
            Reconciliation = cycle.Source == DetectionSource.Reconciliation,
        };
    }

    public ValueCanonicalizer Canonicalizer { get; }

    public AttributeChangeBuilder Changes { get; }

    public ChangeClassifier Classifier { get; }

    public EventFactory Factory { get; }

    public SnapshotBuilder Builder => _builder;

    /// <summary>Processes one result page and commits it as a chunk. Containers are processed by ascending old-DN depth.</summary>
    public async Task ProcessPageAsync(IReadOnlyList<DirectoryObject> entries, bool containerPass, CancellationToken cancellationToken)
    {
        var guids = entries.Select(e => e.ObjectGuid).OfType<Guid>().Distinct().ToList();
        var snapshots = guids.Count == 0
            ? new Dictionary<Guid, SnapshotObject>()
            : await _services.Store.GetSnapshotsAsync(guids, cancellationToken).ConfigureAwait(false);
        IEnumerable<DirectoryObject> ordered = entries;
        if (containerPass)
        {
            ordered = entries.OrderBy(e => Depth(e.ObjectGuid is { } g && snapshots.TryGetValue(g, out var s) ? s.Dn : e.DistinguishedName));
        }

        foreach (var entry in ordered)
        {
            await ProcessEntryAsync(entry, snapshots, containerPass, cancellationToken).ConfigureAwait(false);
        }

        await _cycle.CommitChunkAsync(_services.Store, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Processes one returned object (section 10.6).</summary>
    public async Task ProcessEntryAsync(DirectoryObject entry, IReadOnlyDictionary<Guid, SnapshotObject> snapshots, bool containerPass, CancellationToken cancellationToken)
    {
        if (entry.ObjectGuid is not { } guid || WatchObjectKinds.FromObjectClasses(entry.GetStrings(Attr.ObjectClass)) is not { } kind)
        {
            return;
        }

        _cycle.Seen.Add(guid);
        _cycle.ObjectsExamined++;
        if (WatchObjectKinds.IsContainer(kind) != containerPass)
        {
            return;
        }

        var settings = _cycle.Settings;
        var critical = settings.IsCritical(entry.DistinguishedName);
        if (critical)
        {
            entry = await WithSecurityDescriptorAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        snapshots.TryGetValue(guid, out var old);
        var watched = settings.Watched(kind, critical);
        var current = _builder.Build(entry, kind, watched, old, _cycle.Now)!;
        var oldDn = old is null ? null : _cycle.Rewriter.Rewrite(old.Dn);
        if (containerPass && old is { IsDeleted: false } && oldDn is not null && !DistinguishedName.AreEqual(oldDn, entry.DistinguishedName))
        {
            _cycle.Rewriter.Add(oldDn, entry.DistinguishedName);
        }

        if (!_cycle.Scope.IsInScope(entry.DistinguishedName, kind))
        {
            await LeaveScopeAsync(old, current, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (old is null)
        {
            await AddNewAsync(entry, current, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (old.IsDeleted)
        {
            await RestoreAsync(entry, old, current, watched, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (old.CreationPendingUntil is not null)
        {
            _cycle.Chunk.Upsert(current);
            await UpdateMembersAsync(entry, current, old, emit: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        await DiffAsync(entry, old, oldDn!, current, watched, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits the drafts of one object as events of one change set into the current chunk (kinds that are not recorded
    /// are dropped here; nothing is emitted at Baseline).
    /// </summary>
    public void Emit(IReadOnlyList<EventDraft> drafts, SnapshotObject? subject, ObjectRef objectRef, ReplicationMetadata metadata,
        DateTimeOffset? localChangeTime, DetectionSource? source = null, bool longDisabled = false)
    {
        if (!_cycle.EmitEvents || drafts.Count == 0)
        {
            return;
        }

        var changeSet = Factory.NewChangeSetId();
        var context = new EventContext
        {
            Source = source ?? _cycle.Source,
            Domain = _cycle.Settings.Facts.DomainDnsName,
            Severity = _cycle.Settings.Severity,
            Metadata = metadata,
            Watermark = _cycle.MetadataWatermark,
            LocalChangeTime = localChangeTime,
            ResolveDcHost = (invocation, dsa) => _services.DomainControllers.ResolveHost(invocation) ?? _services.DomainControllers.ResolveHostByDsaDn(dsa),
        };
        foreach (var draft in drafts)
        {
            if (!_cycle.Settings.IsRecorded(draft.Kind))
            {
                continue;
            }

            var tags = _services.Tagger.Tag(draft, subject, draft.Object ?? objectRef, _tagging with
            {
                Reconciliation = (source ?? _cycle.Source) == DetectionSource.Reconciliation,
            }, longDisabled);
            _cycle.Chunk.Add(Factory.Create(draft, objectRef, context, tags, changeSet));
        }
    }

    /// <summary>
    /// The deferred Created change set of a settled (or deleted-while-settling) object (10.6.3): Created with the final
    /// values, risky UAC bits, one GroupMemberAdded per member.
    /// </summary>
    public async Task EmitCreatedAsync(SnapshotObject created, CancellationToken cancellationToken)
    {
        var drafts = new List<EventDraft>(Classifier.ClassifyCreated(created));
        if (created.Kind == WatchObjectKind.Group)
        {
            var members = _cycle.Chunk.PendingMembers(created.ObjectGuid)
                ?? await _services.Store.GetMembersAsync(created.ObjectGuid, cancellationToken).ConfigureAwait(false);
            var known = await KnownMembersAsync(members, cancellationToken).ConfigureAwait(false);
            drafts.AddRange(_services.MemberDiffer.Diff([], members, known, _ => false));
        }

        var metadata = await ReadMetadataAsync(created.Dn, cancellationToken).ConfigureAwait(false);
        Emit(drafts, created, created.ToRef(), metadata, created.WhenCreated);
    }

    /// <summary>Snapshots of the members with GUIDs (display references).</summary>
    public async Task<IReadOnlyDictionary<Guid, SnapshotObject>> KnownMembersAsync(IEnumerable<MemberReference> members, CancellationToken cancellationToken)
    {
        var guids = members.Select(m => m.ObjectGuid).OfType<Guid>().Distinct().ToList();
        return guids.Count == 0
            ? new Dictionary<Guid, SnapshotObject>()
            : await _services.Store.GetSnapshotsAsync(guids, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Metadata of an object; refreshes the DC directory once per cycle when an originating DC is unknown.</summary>
    public async Task<ReplicationMetadata> ReadMetadataAsync(string dn, CancellationToken cancellationToken)
    {
        var metadata = await _services.Metadata.ReadAsync(_cycle.Session, dn, cancellationToken).ConfigureAwait(false);
        if (!_dcDirectoryRefreshed && metadata.Entries.Any(e => _services.DomainControllers.ResolveHost(e.OriginatingInvocationId) is null))
        {
            _dcDirectoryRefreshed = true;
            try
            {
                await _services.DomainControllers.RefreshAsync(_cycle.Session, cancellationToken).ConfigureAwait(false);
            }
            catch (DirectoryException ex) when (!ex.IsTransient)
            {
                _cycle.Warnings.Add("dcDirectoryUnreadable");
            }
        }

        return metadata;
    }

    private async Task LeaveScopeAsync(SnapshotObject? old, SnapshotObject current, CancellationToken cancellationToken)
    {
        if (old is null)
        {
            return;
        }

        if (!old.IsDeleted)
        {
            var draft = new EventDraft { Kind = WatchEventKind.ObjectMoved, PreviousDn = old.Dn, ExtraTags = [EventTags.OutOfScope], MetadataAttributes = [Attr.Name] };
            Emit([draft], current, current.ToRef(), ReplicationMetadata.Empty, null);
        }

        _cycle.Chunk.Remove(current.ObjectGuid);
        if (WatchObjectKinds.IsPrincipal(current.Kind))
        {
            _cycle.Chunk.MembershipRelevant = true;
            _cycle.PrivilegeRelevant = true;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task AddNewAsync(DirectoryObject entry, SnapshotObject current, CancellationToken cancellationToken)
    {
        if (_cycle.Mode == DetectionMode.Baseline)
        {
            _cycle.Chunk.Upsert(current);
            await UpdateMembersAsync(entry, current, null, emit: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (IsCreatedSinceLastPoll(current))
        {
            var pendingUntil = (current.WhenCreated ?? _cycle.Now) + _cycle.Settings.Directory.CreationSettleWindow;
            _cycle.Chunk.Upsert(current with { CreationPendingUntil = pendingUntil });
            await UpdateMembersAsync(entry, current, null, emit: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        var draft = new EventDraft
        {
            Kind = WatchEventKind.ObjectAppeared,
            ExtraTags = _cycle.Settings.ExcludedSubtrees.Count > 0 ? [EventTags.OutOfScope] : [],
        };
        Emit([draft], current, current.ToRef(), ReplicationMetadata.Empty, entry.GetGeneralizedTime(Attr.WhenChanged));
        _cycle.Chunk.Upsert(current);
        await UpdateMembersAsync(entry, current, null, emit: false, cancellationToken).ConfigureAwait(false);
        if (WatchObjectKinds.IsPrincipal(current.Kind))
        {
            _cycle.PrivilegeRelevant = true;
        }
    }

    /// <summary>A new GUID is a creation when uSNCreated &gt; W on this DC (Resync: created after the last poll).</summary>
    private bool IsCreatedSinceLastPoll(SnapshotObject current)
    {
        if (_cycle.Mode == DetectionMode.Resync)
        {
            var since = _cycle.State?.LastPollAt ?? _cycle.State?.LastFullSyncAt;
            return since is not null && current.WhenCreated > since;
        }

        return current.UsnCreated > _cycle.Decision.Watermark;
    }

    private async Task RestoreAsync(DirectoryObject entry, SnapshotObject old, SnapshotObject current, IReadOnlySet<string> watched, CancellationToken cancellationToken)
    {
        var changeSet = Factory.NewChangeSetId();
        var context = _services.Differ.Diff(old, old.Dn, current, watched);
        var draft = new EventDraft
        {
            Kind = WatchEventKind.ObjectRestored,
            PreviousDn = old.Dn,
            Changes = context.Attributes.Select(Changes.FromDelta).ToList(),
            ChangeSetId = changeSet,
            MetadataAttributes = [Attr.IsDeleted, Attr.Name],
        };
        var metadata = await ReadMetadataAsync(entry.DistinguishedName, cancellationToken).ConfigureAwait(false);
        current = SnapshotBuilder.ApplyMetadata(current, metadata);
        Emit([draft], current, current.ToRef(), metadata, entry.GetGeneralizedTime(Attr.WhenChanged));
        _cycle.Chunk.Upsert(current);
        await UpdateMembersAsync(entry, current, null, emit: false, cancellationToken).ConfigureAwait(false);
        var key = current.ObjectGuid.ToString("N");
        _cycle.RestoredChangeSets[key] = changeSet;
        await ReinsertMembershipsAsync(current, cancellationToken).ConfigureAwait(false);
        _cycle.Chunk.MembershipRelevant = true;
        _cycle.PrivilegeRelevant = true;
    }

    /// <summary>
    /// RestoredMembershipReader (10.9): the Recycle Bin re-activates the restored object's link values without bumping
    /// the groups, so its memberOf (Extended DN, ranged) is read and its key re-inserted into those groups' member lists.
    /// </summary>
    private async Task ReinsertMembershipsAsync(SnapshotObject restored, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        try
        {
            await foreach (var value in _cycle.Session.GetAllValuesAsync(restored.Dn, Attr.MemberOf, new AttributeReadOptions { ExtendedDn = true }, cancellationToken)
                .ConfigureAwait(false))
            {
                values.Add(value);
            }
        }
        catch (DirectoryException ex) when (!ex.IsTransient)
        {
            _cycle.Warnings.Add("restoredMemberOfUnreadable");
            return;
        }

        var reference = new MemberReference { Key = restored.ObjectGuid.ToString("N"), ObjectGuid = restored.ObjectGuid, Sid = restored.Sid, Dn = restored.Dn };
        foreach (var group in values.Select(ValueCanonicalizer.ToMemberReference))
        {
            if (group.ObjectGuid is not { } groupGuid)
            {
                continue;
            }

            var members = _cycle.Chunk.PendingMembers(groupGuid) ?? await _services.Store.GetMembersAsync(groupGuid, cancellationToken).ConfigureAwait(false);
            if (!members.Any(m => string.Equals(m.Key, reference.Key, StringComparison.OrdinalIgnoreCase)))
            {
                _cycle.Chunk.ReplaceMembers(groupGuid, [.. members, reference]);
            }
        }
    }

    private async Task DiffAsync(DirectoryObject entry, SnapshotObject old, string oldDn, SnapshotObject current, IReadOnlySet<string> watched, CancellationToken cancellationToken)
    {
        var diff = _services.Differ.Diff(old, oldDn, current, watched);
        var memberDrafts = await UpdateMembersAsync(entry, current, old, emit: _cycle.EmitEvents, cancellationToken).ConfigureAwait(false);
        if (diff.IsEmpty && memberDrafts.Count == 0)
        {
            if (!SameDnLiteral(old.Dn, current.Dn) && !SameDnLiteral(oldDn, current.Dn))
            {
                _cycle.Chunk.Upsert(current);
            }

            return;
        }

        var metadata = await ReadMetadataAsync(entry.DistinguishedName, cancellationToken).ConfigureAwait(false);
        if (_cycle.VersionAware && !metadata.IsEmpty)
        {
            (diff, current) = DropStaleAttributes(diff, old, current, metadata);
        }

        current = SnapshotBuilder.ApplyMetadata(current, metadata);
        _cycle.Chunk.Upsert(current);
        if (_cycle.Mode == DetectionMode.Baseline || (diff.IsEmpty && memberDrafts.Count == 0))
        {
            return;
        }

        var input = await ClassificationInputAsync(diff, entry, metadata, cancellationToken).ConfigureAwait(false);
        var drafts = new List<EventDraft>(Classifier.Classify(input));
        drafts.AddRange(memberDrafts);
        var longDisabled = drafts.Any(d => d.Kind == WatchEventKind.AccountEnabled)
            && await IsLongDisabledAsync(old, entry.DistinguishedName, cancellationToken).ConfigureAwait(false);
        Emit(drafts, current, current.ToRef(), metadata, entry.GetGeneralizedTime(Attr.WhenChanged), longDisabled: longDisabled);
        if (diff.Get(Attr.PrimaryGroupId) is not null)
        {
            _cycle.Chunk.MembershipRelevant = true;
            _cycle.PrivilegeRelevant = true;
        }

        if (current.Kind == WatchObjectKind.Domain && (diff.Get(WatchdogAttributes.LockoutDuration) is not null || diff.Get(WatchdogAttributes.LockoutObservationWindow) is not null))
        {
            _services.LockoutPolicies.Invalidate();
        }
    }

    /// <summary>Resync/Failback: an attribute whose metadata version is lower than stored is not replicated yet here.</summary>
    private (RawObjectDiff Diff, SnapshotObject Current) DropStaleAttributes(RawObjectDiff diff, SnapshotObject old, SnapshotObject current, ReplicationMetadata metadata)
    {
        var stale = diff.Attributes
            .Where(d => metadata.Get(d.Attribute) is { } entry && old.Attributes.TryGetValue(d.Attribute, out var stored)
                && stored.Version is { } version && entry.Version < version)
            .ToList();
        if (stale.Count == 0)
        {
            return (diff, current);
        }

        _cycle.Warnings.Add("staleDc");
        var attributes = new Dictionary<string, SnapshotAttribute>(current.Attributes, StringComparer.OrdinalIgnoreCase);
        foreach (var delta in stale)
        {
            if (delta.Old is null)
            {
                attributes.Remove(delta.Attribute);
            }
            else
            {
                attributes[delta.Attribute] = delta.Old;
            }
        }

        return (diff with { Attributes = diff.Attributes.Except(stale).ToList() }, current with { Attributes = attributes });
    }

    private async Task<ClassificationInput> ClassificationInputAsync(RawObjectDiff diff, DirectoryObject entry, ReplicationMetadata metadata, CancellationToken cancellationToken)
    {
        var lockout = LockoutPolicy.Unknown;
        var lockoutsToday = 0;
        if (diff.Get(Attr.LockoutTime) is not null)
        {
            lockout = await _services.LockoutPolicies.ResolveAsync(_cycle.Session, entry.DistinguishedName, cancellationToken).ConfigureAwait(false);
            var local = TimeZoneInfo.ConvertTime(_cycle.Now, _cycle.Settings.TimeZone);
            var midnight = new DateTimeOffset(local.Date, local.Offset);
            lockoutsToday = await _services.Events.CountForObjectAsync(diff.New.ObjectGuid, WatchEventKind.AccountLockedOut, midnight, cancellationToken)
                .ConfigureAwait(false);
        }

        var dnsConflict = diff.New.Kind == WatchObjectKind.Computer && diff.Get(Attr.DnsHostName) is { NewValue: { } dns }
            && await DnsHostNameConflictAsync(dns, diff.New.ObjectGuid, cancellationToken).ConfigureAwait(false);
        var gpoNames = diff.Get(Attr.GpLink) is { } gpLink
            ? await GroupPolicyNamesAsync(gpLink, cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, string>();
        return new ClassificationInput
        {
            Diff = diff,
            Directory = _cycle.Settings.Directory,
            Now = _cycle.Now,
            Metadata = metadata,
            Watermark = _cycle.MetadataWatermark,
            Lockout = lockout,
            LockoutsToday = lockoutsToday,
            IsPrivileged = _cycle.IsPrivileged,
            DomainControllerNames = _services.DomainControllers.NetBiosNames,
            DnsHostNameConflict = dnsConflict,
            GroupPolicyNames = gpoNames,
            DsaHostName = _services.DomainControllers.ResolveHostByDsaDn,
        };
    }

    /// <summary>
    /// Reads and stores a group's members when its membership is tracked; returns the member drafts (only when
    /// <paramref name="emit"/> and an old list exists).
    /// </summary>
    private async Task<IReadOnlyList<EventDraft>> UpdateMembersAsync(DirectoryObject entry, SnapshotObject current, SnapshotObject? old, bool emit, CancellationToken cancellationToken)
    {
        if (current.Kind != WatchObjectKind.Group || !await TracksMembersAsync(current, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var members = await _services.Members.ReadAsync(_cycle.Session, entry, cancellationToken).ConfigureAwait(false);
        var before = old is null ? [] : await _services.Store.GetMembersAsync(current.ObjectGuid, cancellationToken).ConfigureAwait(false);
        if (old is null || !MembershipReader.AreSame(before, members))
        {
            _cycle.Chunk.ReplaceMembers(current.ObjectGuid, members);
        }

        if (MembershipReader.HaveSameKeys(before, members) && old is not null)
        {
            return [];
        }

        _cycle.Chunk.MembershipRelevant = true;
        _cycle.PrivilegeRelevant = true;
        if (!emit || old is null)
        {
            return [];
        }

        var known = await KnownMembersAsync(members.Concat(before), cancellationToken).ConfigureAwait(false);
        var removed = before.Where(b => !members.Any(m => string.Equals(m.Key, b.Key, StringComparison.OrdinalIgnoreCase))).ToList();
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in removed)
        {
            if (await IsGoneAsync(member, cancellationToken).ConfigureAwait(false))
            {
                gone.Add(member.Key);
            }
        }

        return _services.MemberDiffer.Diff(before, members, known, m => gone.Contains(m.Key));
    }

    private async Task<bool> TracksMembersAsync(SnapshotObject group, CancellationToken cancellationToken)
    {
        var settings = _cycle.Settings;
        if (settings.Directory.MembershipTracking == AdMng.Watchdog.Contracts.Configuration.MembershipTracking.AllGroups)
        {
            return true;
        }

        if (settings.Privileged.TierOf(group.Sid, group.SamAccountName, group.Dn) is not null || settings.Privileged.IsWatchList(group.Dn))
        {
            return true;
        }

        var containing = await _services.Store.GetGroupsContainingAsync(group.ObjectGuid.ToString("N"), cancellationToken).ConfigureAwait(false);
        return containing.Count > 0;
    }

    /// <summary>A removed member no longer exists live (deleted in this cycle, or not found): its removal is no event.</summary>
    private async Task<bool> IsGoneAsync(MemberReference member, CancellationToken cancellationToken)
    {
        if (member.ObjectGuid is not { } guid)
        {
            return false;
        }

        if (_cycle.DeletedGuids.Contains(guid))
        {
            return true;
        }

        if (!_exists.TryGetValue(guid, out var exists))
        {
            var found = false;
            await foreach (var _ in _cycle.Session.SearchAsync(new DirectorySearch
            {
                BaseDn = _cycle.Settings.Facts.DomainDn,
                Filter = LdapFilter.EqualGuid(Attr.ObjectGuid, guid),
                Attributes = [Attr.ObjectGuid],
                SizeLimit = 1,
            }, cancellationToken).ConfigureAwait(false))
            {
                found = true;
            }

            _exists[guid] = exists = found;
        }

        return !exists;
    }

    private async Task<bool> IsLongDisabledAsync(SnapshotObject old, string dn, CancellationToken cancellationToken)
    {
        if (old.Attributes.TryGetValue(Attr.UserAccountControl, out var uac) && uac.ChangedAt is { } changedAt && changedAt <= _cycle.Now - LongDisabledAge)
        {
            return true;
        }

        var entry = await _cycle.Session.GetAsync(dn, [Attr.LastLogonTimestamp], cancellationToken).ConfigureAwait(false);
        var lastLogon = entry?.GetFileTime(Attr.LastLogonTimestamp);
        return lastLogon is null || lastLogon < _cycle.Now - LastLogonLag;
    }

    private async Task<bool> DnsHostNameConflictAsync(string dnsHostName, Guid self, CancellationToken cancellationToken)
    {
        await foreach (var other in _cycle.Session.SearchAsync(new DirectorySearch
        {
            BaseDn = _cycle.Settings.Facts.DomainDn,
            Filter = LdapFilter.Equal(Attr.DnsHostName, dnsHostName),
            Attributes = [Attr.ObjectGuid],
            SizeLimit = 5,
        }, cancellationToken).ConfigureAwait(false))
        {
            if (other.ObjectGuid is { } guid && guid != self)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<IReadOnlyDictionary<string, string>> GroupPolicyNamesAsync(AttributeDelta gpLink, CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in GpLinks.Parse(gpLink.OldValue).Concat(GpLinks.Parse(gpLink.NewValue)))
        {
            var key = ValueCanonicalizer.NormalizeDn(link.Dn);
            if (!names.ContainsKey(key) && await _services.Store.FindByDnAsync(link.Dn, cancellationToken).ConfigureAwait(false) is { DisplayName: { } name })
            {
                names[key] = name;
            }
        }

        return names;
    }

    /// <summary>Critical objects: nTSecurityDescriptor read separately (owner + DACL only).</summary>
    private async Task<DirectoryObject> WithSecurityDescriptorAsync(DirectoryObject entry, CancellationToken cancellationToken)
    {
        await foreach (var withSd in _cycle.Session.SearchAsync(new DirectorySearch
        {
            BaseDn = entry.DistinguishedName,
            Scope = SearchScope.Base,
            Attributes = [Attr.NtSecurityDescriptor],
            ReadSecurityDescriptor = true,
        }, cancellationToken).ConfigureAwait(false))
        {
            if (withSd.TryGet(Attr.NtSecurityDescriptor, out var sd))
            {
                return entry.With([sd]);
            }
        }

        return entry;
    }

    private static int Depth(string dn) => DistinguishedName.TryParse(dn, out var parsed) ? parsed.Rdns.Count : int.MaxValue;

    private static bool SameDnLiteral(string? a, string b) => string.Equals(a, b, StringComparison.Ordinal);
}
