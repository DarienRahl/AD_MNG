using System.Collections.Concurrent;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Security;

namespace AdMng.Core.GroupPolicy.Diagnostics;

/// <summary>
/// Reads what <see cref="GroupPolicyDiagnostics.Analyze"/> needs (design note docs/dev/k-computers-and-gpo.md 20.2), all from
/// the session's DC and its SYSVOL (CLAUDE.md rule 4), into a <see cref="GpoDiagnosticsSnapshot"/> kept in memory only
/// (rule 1); every filter through <see cref="LdapFilter"/> (rule 3), cancellation everywhere (rule 6).
/// <list type="bullet">
/// <item>Quick: the domain head (SID, Domain Controllers OU), the GPOs, the linking containers, the WMI filters
/// (<see cref="GroupPolicyReader"/>) and the <c>GPT.INI</c> of each GPO in scope.</item>
/// <item>Full, in addition: the folders (<see cref="GroupPolicyReader.ReadFolderAsync"/> and the folder ACL) - for one GPO
/// also the folders of the GPOs reaching its containers, for the conflicts; one paged search of the users and computers
/// (the whole domain, or the subtrees of the GPO's links) counted by parent container, attributes <c>sAMAccountType</c>
/// only; the domain SIDs of the permissions, rights and groups looked up by <c>objectSid</c>, 50 per OR search.</item>
/// </list>
/// SYSVOL is read at most 4 requests at a time. A folder that cannot be read is recorded with its status; only cancellation
/// stops the read.
/// </summary>
public static class GroupPolicyDiagnosticsReader
{
    private const int PageSize = 500;
    private const int SysvolParallelism = 4;
    private const int SidChunk = 50;

    /// <summary>The snapshot of <paramref name="request"/>.</summary>
    /// <param name="session">The connection window's session.</param>
    /// <param name="sysvol">
    /// SYSVOL of the session's DC; null skips the GPT.INI versions and the folders. The folders need a connection that lists
    /// them (<see cref="ISysvolWriteConnection"/>, opened by <see cref="ISysvolFileWriter.OpenForWriteAsync"/>); with a
    /// read-only connection a Full read reports the folder rules as not evaluated.
    /// </param>
    /// <param name="request">One GPO or the domain, Quick or Full.</param>
    /// <param name="progress">Steps and counts (optional).</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="DirectoryException">The requested GPO does not exist (<see cref="DirectoryErrorKind.NoSuchObject"/>), or a directory read failed.</exception>
    public static async Task<GpoDiagnosticsSnapshot> ReadAsync(
        IDirectorySession session,
        ISysvolConnection? sysvol,
        GpoDiagnosticsRequest request,
        IProgress<GpoDiagnosticsProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var reader = new GroupPolicyReader();
        var domainDn = session.Info.DefaultNamingContext;
        progress?.Report(new GpoDiagnosticsProgress(GpoDiagnosticsStage.Directory));

        var head = await session.GetAsync(domainDn, [Attr.ObjectSid, .. WellKnownObjects.Attributes], cancellationToken).ConfigureAwait(false);
        var domainSid = head?.ObjectSid;
        var domainControllers = head is null ? null : WellKnownObjectMap.FromDomainObject(head).Find(WellKnownContainer.DomainControllers);
        var gpos = (await reader.ReadPoliciesAsync(session, cancellationToken).ConfigureAwait(false)).ToList();
        if (request.GpoId is { } requested && !gpos.Any(g => g.Id == requested))
        {
            throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "The GPO does not exist.");
        }

        var containers = await reader.ReadLinkingContainersAsync(session, cancellationToken).ConfigureAwait(false);
        var wmiFilters = await reader.ReadWmiFiltersAsync(session, cancellationToken).ConfigureAwait(false);
        var scope = request.GpoId is { } one ? gpos.Where(g => g.Id == one).ToList() : gpos;

        var versionsRead = false;
        if (sysvol is not null)
        {
            progress?.Report(new GpoDiagnosticsProgress(GpoDiagnosticsStage.Versions, 0, scope.Count));
            var withVersions = (await reader.WithSysvolVersionsAsync(sysvol, scope, cancellationToken).ConfigureAwait(false)).ToDictionary(g => g.Id);
            gpos = [.. gpos.Select(g => withVersions.GetValueOrDefault(g.Id) ?? g)];
            scope = request.GpoId is null ? gpos : [.. gpos.Where(g => withVersions.ContainsKey(g.Id))];
            versionsRead = true;
        }

        var snapshot = new GpoDiagnosticsSnapshot
        {
            Depth = request.Depth,
            GpoId = request.GpoId,
            DomainDn = domainDn,
            DomainDnsName = session.Info.DomainDnsName,
            DomainSid = domainSid,
            DomainControllersDn = domainControllers,
            Gpos = gpos,
            Containers = containers,
            WmiFilters = wmiFilters,
            SysvolVersionsRead = versionsRead,
        };
        if (request.Depth == GpoDiagnosticsDepth.Quick)
        {
            progress?.Report(new GpoDiagnosticsProgress(GpoDiagnosticsStage.Completed));
            return snapshot;
        }

        var inheritance = new GroupPolicyInheritance(domainDn, containers.Where(c => c.Kind != GroupPolicySomKind.Site).Select(c => c.ToSom()));
        IReadOnlyDictionary<Guid, GpoFolderState> folders = new Dictionary<Guid, GpoFolderState>();
        if (sysvol is ISysvolWriteConnection writable)
        {
            var others = request.GpoId is { } target ? ContextGpos(target, containers, inheritance, gpos) : [];
            folders = await ReadFoldersAsync(writable, scope.Select(g => g.Id), others, progress, cancellationToken).ConfigureAwait(false);
        }

        var counts = await CountObjectsAsync(session, request.GpoId, containers, progress, cancellationToken).ConfigureAwait(false);
        var existing = await ResolveSidsAsync(session, domainSid, scope, folders, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new GpoDiagnosticsProgress(GpoDiagnosticsStage.Completed));
        return snapshot with
        {
            FoldersRead = sysvol is ISysvolWriteConnection,
            Folders = folders,
            ObjectCounts = counts,
            ExistingSids = existing,
        };
    }

    /// <summary>
    /// For one GPO: the other GPOs in the chains of the containers it reaches (their settings may conflict with its own
    /// or decide the loopback of its computers).
    /// </summary>
    private static IReadOnlyList<Guid> ContextGpos(Guid target, IReadOnlyList<GroupPolicyContainerInfo> containers, GroupPolicyInheritance inheritance, IReadOnlyList<GpoInfo> gpos)
    {
        var existing = gpos.Select(g => g.Id).ToHashSet();
        var result = new HashSet<Guid>();
        foreach (var container in containers.Where(c => c.Kind != GroupPolicySomKind.Site))
        {
            var chain = inheritance.Resolve(container.Dn, siteDn: null);
            if (chain.Any(a => a.GpoId == target))
            {
                result.UnionWith(chain.Select(a => a.GpoId).Where(id => id != target && existing.Contains(id)));
            }
        }

        return [.. result.Order()];
    }

    private static async Task<IReadOnlyDictionary<Guid, GpoFolderState>> ReadFoldersAsync(
        ISysvolWriteConnection sysvol,
        IEnumerable<Guid> inScope,
        IEnumerable<Guid> others,
        IProgress<GpoDiagnosticsProgress>? progress,
        CancellationToken cancellationToken)
    {
        // The GPOs in scope with their ACL, the others (conflicts) for their content only.
        var work = inScope.Distinct().Select(id => (Id: id, Security: true))
            .Concat(others.Distinct().Select(id => (Id: id, Security: false)))
            .DistinctBy(w => w.Id)
            .ToList();
        var result = new ConcurrentDictionary<Guid, GpoFolderState>();
        var done = 0;
        progress?.Report(new GpoDiagnosticsProgress(GpoDiagnosticsStage.Folders, 0, work.Count));
        await using var throttled = new ThrottledSysvolConnection(sysvol, SysvolParallelism);
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = SysvolParallelism, CancellationToken = cancellationToken }, async (item, token) =>
        {
            result[item.Id] = await ReadFolderAsync(throttled, item.Id, item.Security, token).ConfigureAwait(false);
            progress?.Report(new GpoDiagnosticsProgress(GpoDiagnosticsStage.Folders, Interlocked.Increment(ref done), work.Count));
        }).ConfigureAwait(false);
        return new Dictionary<Guid, GpoFolderState>(result);
    }

    private static async Task<GpoFolderState> ReadFolderAsync(ISysvolWriteConnection sysvol, Guid gpoId, bool withSecurity, CancellationToken cancellationToken)
    {
        GpoFolderContent content;
        try
        {
            content = await GroupPolicyReader.ReadFolderAsync(sysvol, gpoId, cancellationToken).ConfigureAwait(false);
        }
        catch (SysvolWriteException ex) when (ex.Error == SysvolWriteError.NotFound)
        {
            return new GpoFolderState { Status = GpoFolderReadStatus.NotFound };
        }
        catch (SysvolWriteException ex) when (ex.Error is SysvolWriteError.AccessDenied or SysvolWriteError.LogonFailure)
        {
            return new GpoFolderState { Status = GpoFolderReadStatus.AccessDenied };
        }
        catch (GpoTooLargeException)
        {
            return new GpoFolderState { Status = GpoFolderReadStatus.TooLarge };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Any other failure of one folder (SYSVOL error, a path SYSVOL rejects …) leaves only that folder unread.
            return new GpoFolderState { Status = GpoFolderReadStatus.Failed };
        }

        byte[]? security = null;
        if (withSecurity)
        {
            try
            {
                security = await sysvol.GetGpoFolderSecurityAsync(gpoId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                security = null; // the ACL rule is skipped for this folder
            }
        }

        return GpoFolderState.Of(content, security, withSecurity);
    }

    /// <summary>
    /// Users and computers by direct parent: the whole domain, or for one GPO the subtrees of its enabled links to the domain
    /// head and OUs (the topmost ones; a container deleted meanwhile is skipped).
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, GpoObjectCount>> CountObjectsAsync(
        IDirectorySession session,
        Guid? gpoId,
        IReadOnlyList<GroupPolicyContainerInfo> containers,
        IProgress<GpoDiagnosticsProgress>? progress,
        CancellationToken cancellationToken)
    {
        var domainDn = session.Info.DefaultNamingContext;
        List<string> bases;
        if (gpoId is { } id)
        {
            var linked = GroupPolicyReader.LinksByGpo(containers).GetValueOrDefault(id) ?? [];
            var roots = linked.Where(l => l.Enabled && l.Kind != GroupPolicySomKind.Site)
                .Select(l => DistinguishedName.TryParse(l.ContainerDn, out var dn) ? dn : null)
                .OfType<DistinguishedName>()
                .Distinct()
                .ToList();
            bases = [.. roots.Where(r => !roots.Any(o => o != r && r.IsDescendantOf(o))).Select(r => r.ToString())];
        }
        else
        {
            bases = [domainDn];
        }

        var counts = new Dictionary<string, (int Users, int Computers)>(StringComparer.OrdinalIgnoreCase);
        var seen = 0;
        progress?.Report(new GpoDiagnosticsProgress(GpoDiagnosticsStage.Objects));
        foreach (var root in bases)
        {
            try
            {
                await foreach (var account in session.SearchAsync(new DirectorySearch
                {
                    BaseDn = root,
                    Scope = SearchScope.Subtree,
                    Filter = LdapFilter.Or(ObjectKind.User.ToLdapFilter(), ObjectKind.Computer.ToLdapFilter()),
                    Attributes = [Attr.SamAccountType],
                    PageSize = PageSize,
                }, cancellationToken).ConfigureAwait(false))
                {
                    if (account.Dn.Parent is not { IsRoot: false } parent)
                    {
                        continue;
                    }

                    var key = parent.ToString();
                    var (users, computers) = counts.GetValueOrDefault(key);
                    counts[key] = account.GetInt32(Attr.SamAccountType) == SamAccountType.MachineAccount ? (users, computers + 1) : (users + 1, computers);
                    if (++seen % 1000 == 0)
                    {
                        progress?.Report(new GpoDiagnosticsProgress(GpoDiagnosticsStage.Objects, seen));
                    }
                }
            }
            catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
            {
                // The container was deleted after the links were read.
            }
        }

        progress?.Report(new GpoDiagnosticsProgress(GpoDiagnosticsStage.Objects, seen));
        return counts.ToDictionary(c => c.Key, c => new GpoObjectCount(c.Value.Users, c.Value.Computers), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The domain SIDs (RID 1000 or above) of the permissions of the GPOs in scope and of the rights and groups of their
    /// folders that exist in the directory; null when the domain SID is unknown or a lookup fails (the rules are then not
    /// evaluated rather than reporting every SID).
    /// </summary>
    private static async Task<IReadOnlySet<string>?> ResolveSidsAsync(
        IDirectorySession session,
        string? domainSid,
        IReadOnlyList<GpoInfo> scope,
        IReadOnlyDictionary<Guid, GpoFolderState> folders,
        IProgress<GpoDiagnosticsProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (domainSid is null)
        {
            return null;
        }

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gpo in scope)
        {
            if (gpo.SecurityDescriptor is { } descriptor && SecurityDescriptorSummary.TryParse(descriptor, out var summary) && summary is not null)
            {
                wanted.UnionWith(summary.Aces.Select(a => a.Sid).Where(s => GpoPrincipals.NeedsLookup(s, domainSid)));
            }

            if (folders.GetValueOrDefault(gpo.Id) is { Status: GpoFolderReadStatus.Read, Content: { } content })
            {
                wanted.UnionWith(new GpoFolderView(content).PrincipalSids().Where(s => GpoPrincipals.NeedsLookup(s, domainSid)));
            }
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        progress?.Report(new GpoDiagnosticsProgress(GpoDiagnosticsStage.Principals, 0, wanted.Count));
        try
        {
            foreach (var chunk in wanted.Order(StringComparer.OrdinalIgnoreCase).Chunk(SidChunk))
            {
                await foreach (var found in session.SearchAsync(new DirectorySearch
                {
                    BaseDn = session.Info.DefaultNamingContext,
                    Scope = SearchScope.Subtree,
                    Filter = LdapFilter.Or(chunk.Select(sid => LdapFilter.Equal(Attr.ObjectSid, sid))),
                    Attributes = [Attr.ObjectSid],
                    PageSize = SidChunk,
                }, cancellationToken).ConfigureAwait(false))
                {
                    if (found.ObjectSid is { } sid)
                    {
                        existing.Add(sid);
                    }
                }

                done += chunk.Length;
                progress?.Report(new GpoDiagnosticsProgress(GpoDiagnosticsStage.Principals, done, wanted.Count));
            }
        }
        catch (DirectoryException)
        {
            return null;
        }

        return existing;
    }
}
