using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Transitive privileged access (section 10.8): roots are the groups of the expanded Critical/High SID lists, the High
/// sAMAccountNames and DNs, plus the watchdog's role and host groups (High); the graph is the stored member lists plus
/// virtual primary-group edges; a BFS from every root (cycle-safe) gives every reached member key – users, computers,
/// service accounts, FSPs and nested groups – with its shortest path and the root's tier. The diff against the stored
/// state by (principal, privileged group) yields PrivilegeGranted (severity = tier) and PrivilegeRevoked (Medium);
/// principals deleted in the cycle produce no revocation, privileges of a restored object carry tag restored and the
/// ObjectRestored change set. Idempotent – safe to rerun after a crash.
/// </summary>
internal sealed class PrivilegeEvaluator
{
    private readonly DetectionServices _services;

    public PrivilegeEvaluator(DetectionServices services)
    {
        _services = services;
    }

    /// <summary>Computes the privilege state; drafts only when <paramref name="emit"/> and a previous state exists.</summary>
    public async Task<PrivilegeEvaluation> EvaluateAsync(DetectionCycle cycle, bool emit, CancellationToken cancellationToken)
    {
        var roots = await FindRootsAsync(cycle, cancellationToken).ConfigureAwait(false);
        var edges = await LoadGraphAsync(cycle, cancellationToken).ConfigureAwait(false);
        var principals = new Dictionary<(string Key, Guid Group), PrivilegedPrincipal>();
        foreach (var (rootGuid, tier) in roots)
        {
            Traverse(rootGuid, tier, edges, principals);
        }

        var state = new PrivilegeState { Principals = [.. principals.Values], ComputedAt = _services.Time.GetUtcNow() };
        if (!emit || cycle.StoredPrivileges is not { } previous)
        {
            return new PrivilegeEvaluation(state, []);
        }

        var before = previous.Principals.ToDictionary(p => (p.PrincipalKey.ToUpperInvariant(), p.GroupGuid));
        var after = principals.Values.ToDictionary(p => (p.PrincipalKey.ToUpperInvariant(), p.GroupGuid));
        var granted = after.Where(p => !before.ContainsKey(p.Key)).Select(p => p.Value).ToList();
        var revoked = before.Where(p => !after.ContainsKey(p.Key) && !cycle.DeletedPrincipalKeys.Contains(p.Value.PrincipalKey)).Select(p => p.Value).ToList();
        if (granted.Count == 0 && revoked.Count == 0)
        {
            return new PrivilegeEvaluation(state, []);
        }

        var guids = granted.Concat(revoked)
            .SelectMany(p => p.Path.Append(p.GroupGuid).Append(KeyGuid(p.PrincipalKey) ?? Guid.Empty))
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();
        var snapshots = await _services.Store.GetSnapshotsAsync(guids, cancellationToken).ConfigureAwait(false);
        var drafts = new List<EventDraft>();
        drafts.AddRange(granted.Select(p => Draft(WatchEventKind.PrivilegeGranted, p, snapshots, cycle, edges)));
        drafts.AddRange(revoked.Select(p => Draft(WatchEventKind.PrivilegeRevoked, p, snapshots, cycle, edges)));
        return new PrivilegeEvaluation(state, drafts);
    }

    private static void Traverse(Guid root, PrivilegeTier tier, Dictionary<Guid, List<(MemberReference Member, bool ViaPrimaryGroup)>> edges,
        Dictionary<(string Key, Guid Group), PrivilegedPrincipal> principals)
    {
        var visited = new HashSet<Guid> { root };
        var queue = new Queue<(Guid Group, IReadOnlyList<Guid> Path)>();
        queue.Enqueue((root, []));
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var (group, path) = queue.Dequeue();
            if (!edges.TryGetValue(group, out var members))
            {
                continue;
            }

            foreach (var (member, viaPrimary) in members)
            {
                if (reached.Add(member.Key))
                {
                    var key = (member.Key, root);
                    var principal = new PrivilegedPrincipal { PrincipalKey = member.Key, GroupGuid = root, Tier = tier, Path = path, ViaPrimaryGroup = viaPrimary };
                    if (!principals.TryGetValue(key, out var existing) || existing.Tier < tier)
                    {
                        principals[key] = principal;
                    }
                }

                if (member.ObjectGuid is { } nested && nested != root && visited.Add(nested) && edges.ContainsKey(nested))
                {
                    queue.Enqueue((nested, [.. path, nested]));
                }
            }
        }
    }

    /// <summary>Root groups by SID, sAMAccountName and DN with their tier (a group in both lists is Critical).</summary>
    private async Task<Dictionary<Guid, PrivilegeTier>> FindRootsAsync(DetectionCycle cycle, CancellationToken cancellationToken)
    {
        var privileged = cycle.Settings.Privileged;
        var candidates = new Dictionary<Guid, SnapshotObject>();
        foreach (var sid in privileged.RootSids)
        {
            if (await _services.Store.FindBySidAsync(sid, cancellationToken).ConfigureAwait(false) is { IsDeleted: false } group)
            {
                candidates[group.ObjectGuid] = group;
            }
        }

        foreach (var dn in privileged.RootDns)
        {
            if (await _services.Store.FindByDnAsync(dn.ToString(), cancellationToken).ConfigureAwait(false) is { IsDeleted: false } group)
            {
                candidates[group.ObjectGuid] = group;
            }
        }

        if (privileged.RootSamAccountNames.Count > 0)
        {
            var filter = LdapFilter.And(
                LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Group),
                LdapFilter.Or(privileged.RootSamAccountNames.Select(n => LdapFilter.Equal(Attr.SamAccountName, n))));
            var guids = new List<Guid>();
            await foreach (var entry in cycle.Session.SearchAsync(new DirectorySearch
            {
                BaseDn = cycle.Settings.Facts.DomainDn,
                Filter = filter,
                Attributes = [Attr.ObjectGuid],
            }, cancellationToken).ConfigureAwait(false))
            {
                if (entry.ObjectGuid is { } guid)
                {
                    guids.Add(guid);
                }
            }

            if (guids.Count > 0)
            {
                foreach (var group in (await _services.Store.GetSnapshotsAsync(guids, cancellationToken).ConfigureAwait(false)).Values.Where(g => !g.IsDeleted))
                {
                    candidates[group.ObjectGuid] = group;
                }
            }
        }

        var roots = new Dictionary<Guid, PrivilegeTier>();
        foreach (var group in candidates.Values.Where(g => g.Kind == WatchObjectKind.Group))
        {
            if (privileged.TierOf(group.Sid, group.SamAccountName, group.Dn) is { } tier)
            {
                roots[group.ObjectGuid] = tier;
            }
        }

        return roots;
    }

    /// <summary>
    /// Stored member edges plus virtual primary-group edges (principal's primaryGroupID RID + domain SID → group). In
    /// PrivilegedAndWatchListOnly mode, nested groups without a stored list are read on demand (not stored).
    /// </summary>
    private async Task<Dictionary<Guid, List<(MemberReference Member, bool ViaPrimaryGroup)>>> LoadGraphAsync(DetectionCycle cycle, CancellationToken cancellationToken)
    {
        var edges = new Dictionary<Guid, List<(MemberReference, bool)>>();
        foreach (var (group, member) in await _services.Store.GetAllMembershipsAsync(cancellationToken).ConfigureAwait(false))
        {
            Edge(edges, group).Add((member, false));
        }

        var domainSid = cycle.Settings.Facts.DomainSid;
        if (domainSid is not null)
        {
            var groupsBySid = new Dictionary<uint, Guid?>();
            foreach (var (memberKey, rid) in await _services.Store.GetNonDefaultPrimaryGroupsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!groupsBySid.TryGetValue(rid, out var groupGuid))
                {
                    var sid = string.Create(CultureInfo.InvariantCulture, $"{domainSid}-{rid}");
                    groupGuid = (await _services.Store.FindBySidAsync(sid, cancellationToken).ConfigureAwait(false))?.ObjectGuid;
                    groupsBySid[rid] = groupGuid;
                }

                if (groupGuid is { } target)
                {
                    var member = new MemberReference { Key = memberKey, ObjectGuid = KeyGuid(memberKey), Dn = string.Empty };
                    Edge(edges, target).Add((member, true));
                }
            }
        }

        if (cycle.Settings.Directory.MembershipTracking == MembershipTracking.PrivilegedAndWatchListOnly)
        {
            await ReadUntrackedNestedGroupsAsync(cycle, edges, cancellationToken).ConfigureAwait(false);
        }

        return edges;
    }

    private async Task ReadUntrackedNestedGroupsAsync(DetectionCycle cycle, Dictionary<Guid, List<(MemberReference Member, bool ViaPrimaryGroup)>> edges, CancellationToken cancellationToken)
    {
        var pending = new Queue<Guid>(edges.Values.SelectMany(l => l).Select(m => m.Member.ObjectGuid).OfType<Guid>().Where(g => !edges.ContainsKey(g)).Distinct());
        var checkedGuids = new HashSet<Guid>();
        while (pending.Count > 0)
        {
            var batch = new List<Guid>();
            while (pending.Count > 0 && batch.Count < 200)
            {
                var guid = pending.Dequeue();
                if (checkedGuids.Add(guid))
                {
                    batch.Add(guid);
                }
            }

            var snapshots = await _services.Store.GetSnapshotsAsync(batch, cancellationToken).ConfigureAwait(false);
            foreach (var group in snapshots.Values.Where(s => s.Kind == WatchObjectKind.Group && !s.IsDeleted))
            {
                var entry = await ReadGroupAsync(cycle, group.Dn, cancellationToken).ConfigureAwait(false);
                if (entry is null)
                {
                    continue;
                }

                var members = await _services.Members.ReadAsync(cycle.Session, entry, cancellationToken).ConfigureAwait(false);
                edges[group.ObjectGuid] = members.Select(m => (m, false)).ToList();
                foreach (var nested in members.Select(m => m.ObjectGuid).OfType<Guid>().Where(g => !edges.ContainsKey(g) && !checkedGuids.Contains(g)))
                {
                    pending.Enqueue(nested);
                }
            }
        }
    }

    private static async Task<AdMng.Core.Model.DirectoryObject?> ReadGroupAsync(DetectionCycle cycle, string dn, CancellationToken cancellationToken)
    {
        await foreach (var entry in cycle.Session.SearchAsync(new DirectorySearch
        {
            BaseDn = dn,
            Scope = SearchScope.Base,
            Attributes = [Attr.Member],
            ExtendedDn = true,
        }, cancellationToken).ConfigureAwait(false))
        {
            return entry;
        }

        return null;
    }

    private static EventDraft Draft(WatchEventKind kind, PrivilegedPrincipal principal, IReadOnlyDictionary<Guid, SnapshotObject> snapshots,
        DetectionCycle cycle, Dictionary<Guid, List<(MemberReference Member, bool ViaPrimaryGroup)>> edges)
    {
        var principalRef = ReferenceOf(principal.PrincipalKey, snapshots, edges);
        var groupRef = snapshots.TryGetValue(principal.GroupGuid, out var group) ? group.ToRef() : new ObjectRef { ObjectGuid = principal.GroupGuid, Dn = string.Empty };
        var via = principal.Path.Select(g => snapshots.TryGetValue(g, out var s) ? s.ToRef() : new ObjectRef { ObjectGuid = g, Dn = string.Empty }).ToList();
        var restored = kind == WatchEventKind.PrivilegeGranted && cycle.RestoredChangeSets.TryGetValue(principal.PrincipalKey, out var changeSet) ? changeSet : (Guid?)null;
        return new EventDraft
        {
            Kind = kind,
            Object = principalRef,
            Member = groupRef,
            Via = via,
            BaseSeverity = kind == WatchEventKind.PrivilegeGranted ? (principal.Tier == PrivilegeTier.Critical ? Severity.Critical : Severity.High) : Severity.Medium,
            ExtraTags = restored is null ? [] : [EventTags.Restored],
            ChangeSetId = restored,
            MetadataAttributes = [Attr.Member],
        };
    }

    private static ObjectRef ReferenceOf(string key, IReadOnlyDictionary<Guid, SnapshotObject> snapshots, Dictionary<Guid, List<(MemberReference Member, bool ViaPrimaryGroup)>> edges)
    {
        if (KeyGuid(key) is { } guid && snapshots.TryGetValue(guid, out var snapshot))
        {
            return snapshot.ToRef();
        }

        var member = edges.Values.SelectMany(l => l).Select(m => m.Member).FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase) && m.Dn.Length > 0);
        return new ObjectRef
        {
            ObjectGuid = KeyGuid(key),
            Sid = member?.Sid ?? (key.StartsWith(ValueCanonicalizer.SidPrefix, StringComparison.Ordinal) ? key[ValueCanonicalizer.SidPrefix.Length..] : null),
            Dn = member?.Dn ?? string.Empty,
            Name = member is not null && DistinguishedName.TryParse(member.Dn, out var dn) && !dn.IsRoot ? dn.Name : null,
        };
    }

    private static Guid? KeyGuid(string key) => key.Length == 32 && Guid.TryParseExact(key, "N", out var guid) ? guid : null;

    private static List<(MemberReference, bool)> Edge(Dictionary<Guid, List<(MemberReference, bool)>> edges, Guid group)
    {
        if (!edges.TryGetValue(group, out var list))
        {
            edges[group] = list = [];
        }

        return list;
    }
}
