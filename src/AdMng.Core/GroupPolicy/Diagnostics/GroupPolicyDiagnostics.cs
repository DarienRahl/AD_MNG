using System.Globalization;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Ldap;
using AdMng.Core.Security;

namespace AdMng.Core.GroupPolicy.Diagnostics;

/// <summary>
/// "Sprawdź zasady grupy" (K6, design note docs/dev/k-computers-and-gpo.md 20.2): the logical errors and weak security
/// settings of the GPOs of a snapshot. Pure - <see cref="GroupPolicyDiagnosticsReader"/> reads the snapshot. Every rule of
/// the note's table is here with its severity, area (<see cref="GpoFindingCodes.All"/>) and repair; a rule whose data the
/// snapshot does not hold (Quick depth, no SYSVOL, a failed lookup) is skipped and its group reported in
/// <see cref="GpoDiagnosticsResult.NotEvaluated"/>. For one GPO (<see cref="GpoDiagnosticsSnapshot.GpoId"/>) only the
/// findings about that GPO are returned (conflicts: as winner or loser); the container and WMI filter findings
/// (<c>LinkToMissingGpo</c>, <c>LinkMalformed</c>, <c>WmiFilterUnused</c>) belong to the domain check.
/// </summary>
public static partial class GroupPolicyDiagnostics
{
    /// <summary>The findings of <paramref name="snapshot"/>, most severe first.</summary>
    public static GpoDiagnosticsResult Analyze(GpoDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var context = new Context(snapshot);
        LinkRules(context);
        StatusRules(context);
        FilteringRules(context);
        if (context.Evaluates(GpoRuleGroup.SysvolVersions) || context.Evaluates(GpoRuleGroup.Folders))
        {
            VersionAndFolderPresenceRules(context);
        }

        if (context.Evaluates(GpoRuleGroup.Folders))
        {
            foreach (var gpo in context.InScope)
            {
                if (context.View(gpo.Id) is { } view)
                {
                    FolderRules(context, gpo, view);
                    ContentRules(context, gpo, view);
                }
            }
        }

        if (context.Evaluates(GpoRuleGroup.ObjectCounts))
        {
            ScopeRules(context);
        }

        if (context.Evaluates(GpoRuleGroup.Conflicts))
        {
            ConflictRules(context);
        }

        return context.Result();
    }

    // ---- Links and status ---------------------------------------------------------------------------------------------

    private static void LinkRules(Context c)
    {
        foreach (var gpo in c.InScope)
        {
            var links = c.LinksOf(gpo.Id);
            if (links.Count == 0)
            {
                c.Add(GpoFindingCodes.GpoNotLinked, gpo.Id);
            }
            else if (links.All(l => !l.Enabled))
            {
                var first = links[0];
                c.Add(GpoFindingCodes.GpoLinksAllDisabled, gpo.Id, first.ContainerDn, arguments: [Number(links.Count)],
                    fix: new GpoQuickFix.EnableLink(first.ContainerDn, gpo.Id));
            }

            if (c.IsLinked(gpo.Id))
            {
                if (gpo.Status == GpoStatus.AllSettingsDisabled)
                {
                    c.Add(GpoFindingCodes.LinkedAllSettingsDisabled, gpo.Id);
                }

                if (c.VersionOf(gpo) is { User: 0, Computer: 0 })
                {
                    c.Add(GpoFindingCodes.EmptyGpoLinked, gpo.Id);
                }
            }
        }

        foreach (var container in c.Snapshot.Containers)
        {
            var own = container.Links.Links;
            foreach (var duplicate in own.GroupBy(l => l.GpoId).Where(g => g.Count() > 1 && c.InScopeId(g.Key)))
            {
                c.Add(GpoFindingCodes.DuplicateLink, duplicate.Key, container.Dn, arguments: [Number(duplicate.Count())],
                    fix: new GpoQuickFix.RemoveDuplicateLinks(container.Dn, duplicate.Key));
            }

            if (!c.IsDomainScope)
            {
                continue;
            }

            foreach (var missing in own.Select(l => l.GpoId).Distinct().Where(id => !c.Exists(id)))
            {
                c.Add(GpoFindingCodes.LinkToMissingGpo, missing, container.Dn, arguments: [SysvolPaths.FolderName(missing)],
                    fix: new GpoQuickFix.Unlink(container.Dn, missing));
            }

            // Entries that are not links of this domain: GPOs of other domains (normal for a site) or text that cannot be read.
            var other = container.Links.Count - own.Count;
            if (other > 0)
            {
                var foreign = GroupPolicyLinks.Parse(container.RawGpLink).Count(l => !l.IsInDomain(c.DomainDn));
                var unreadable = Math.Max(0, other - foreign);
                foreign = other - unreadable;
                if (container.Kind == GroupPolicySomKind.Site)
                {
                    foreign = 0;
                }

                if (unreadable > 0 || foreign > 0)
                {
                    c.Add(GpoFindingCodes.LinkMalformed, null, container.Dn, arguments: [Number(unreadable), Number(foreign)]);
                }
            }
        }
    }

    private static void StatusRules(Context c)
    {
        foreach (var gpo in c.InScope)
        {
            if (c.VersionOf(gpo) is not { } version)
            {
                continue;
            }

            var status = gpo.Status;
            if (status == GpoStatus.UserSettingsDisabled && version.User > 0)
            {
                c.Add(GpoFindingCodes.PartDisabledWithSettings, gpo.Id, arguments: ["User", Number(version.User)],
                    fix: new GpoQuickFix.SetStatus(gpo.Id, GpoStatus.AllSettingsEnabled));
            }
            else if (status == GpoStatus.ComputerSettingsDisabled && version.Computer > 0)
            {
                c.Add(GpoFindingCodes.PartDisabledWithSettings, gpo.Id, arguments: ["Computer", Number(version.Computer)],
                    fix: new GpoQuickFix.SetStatus(gpo.Id, GpoStatus.AllSettingsEnabled));
            }

            if (!c.IsLinked(gpo.Id))
            {
                continue;
            }

            if (UserEnabled(gpo) && version.User == 0 && version.Computer > 0)
            {
                c.Add(GpoFindingCodes.PartEnabledWithoutSettings, gpo.Id, arguments: ["User"],
                    fix: new GpoQuickFix.SetStatus(gpo.Id, (GpoStatus)((int)status | (int)GpoStatus.UserSettingsDisabled)));
            }

            if (ComputerEnabled(gpo) && version.Computer == 0 && version.User > 0)
            {
                c.Add(GpoFindingCodes.PartEnabledWithoutSettings, gpo.Id, arguments: ["Computer"],
                    fix: new GpoQuickFix.SetStatus(gpo.Id, (GpoStatus)((int)status | (int)GpoStatus.ComputerSettingsDisabled)));
            }
        }
    }

    // ---- Security filtering, permissions, WMI filters, path -----------------------------------------------------------

    private static void FilteringRules(Context c)
    {
        var computerToken = Modeling.GroupPolicyModelReader.ContainerTarget(c.DomainDn, Modeling.ModelingPart.Computer, c.DomainSid).Token;
        var broad = GpoPrincipals.BroadSids(c.DomainSid);
        foreach (var gpo in c.InScope)
        {
            var linked = c.IsLinked(gpo.Id);
            if (gpo.SecurityFilter is { } filter)
            {
                if (linked && !filter.Allowed.Any(s => !filter.Denied.Contains(s)))
                {
                    c.Add(GpoFindingCodes.NoApplyRight, gpo.Id);
                }

                if (linked && filter.KnowsReaders && UserEnabled(gpo) && c.VersionOf(gpo) is { User: > 0 } && !filter.CanRead(computerToken))
                {
                    c.Add(GpoFindingCodes.Ms16072ComputersCannotRead, gpo.Id,
                        fix: new GpoQuickFix.GrantRead(gpo.Id, GpoPrincipals.AuthenticatedUsers));
                }
            }

            if (gpo.SecurityDescriptor is { } descriptor && SecurityDescriptorSummary.TryParse(descriptor, out var summary) && summary is not null)
            {
                var effective = summary.Aces.Where(a => !a.IsInheritOnly).ToList();
                foreach (var sid in effective.Where(a => a.IsDeny).Select(a => a.Sid).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    c.Add(GpoFindingCodes.DenyInSecurityFiltering, gpo.Id, arguments: [sid]);
                }

                foreach (var sid in effective.Where(a => a.IsAllow && broad.Contains(a.Sid) && CanEdit(a.Mask)).Select(a => a.Sid).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    c.Add(GpoFindingCodes.BroadEditRights, gpo.Id, arguments: [sid]);
                }

                if (c.Evaluates(GpoRuleGroup.Principals))
                {
                    foreach (var trustee in summary.Aces.GroupBy(a => a.Sid, StringComparer.OrdinalIgnoreCase)
                        .Where(g => GpoPrincipals.IsUnresolved(g.Key, c.DomainSid, c.Snapshot.ExistingSids!)))
                    {
                        c.Add(GpoFindingCodes.OrphanedTrustee, gpo.Id, arguments: [trustee.Key],
                            fix: trustee.Any(a => !a.IsInherited) ? new GpoQuickFix.RemoveTrustee(gpo.Id, trustee.Key) : null);
                    }
                }
            }

            if (gpo.WmiFilterId is { } filterId && !c.Snapshot.WmiFilters.Any(f => string.Equals(f.Id, filterId, StringComparison.OrdinalIgnoreCase)))
            {
                c.Add(GpoFindingCodes.WmiFilterMissing, gpo.Id, arguments: [filterId], fix: new GpoQuickFix.ClearWmiFilter(gpo.Id));
            }

            if (IsPathRedirected(gpo, c.Snapshot.DomainDnsName))
            {
                c.Add(GpoFindingCodes.PathRedirected, gpo.Id, arguments: [gpo.FileSysPath!]);
            }
        }

        if (c.IsDomainScope)
        {
            var used = c.Snapshot.Gpos.Select(g => g.WmiFilterId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var filter in c.Snapshot.WmiFilters.Where(f => !used.Contains(f.Id)))
            {
                c.Add(GpoFindingCodes.WmiFilterUnused, null, arguments: [filter.Name, filter.Id], fix: new GpoQuickFix.DeleteWmiFilter(filter.Dn));
            }
        }
    }

    /// <summary>Write property (any), write DACL or write owner on the container: enough to change what the GPO does.</summary>
    private static bool CanEdit(int mask) =>
        (DirectoryAccessMask.Normalize(mask) & (DirectoryAccessMask.WriteProperty | DirectoryAccessMask.WriteDacl | DirectoryAccessMask.WriteOwner)) != 0;

    /// <summary>gPCFileSysPath is set and is not <c>\\&lt;domain&gt;\SysVol\&lt;domain&gt;\Policies\{GUID}</c> (case and a trailing '\' do not matter).</summary>
    private static bool IsPathRedirected(GpoInfo gpo, string domainDnsName)
    {
        if (string.IsNullOrWhiteSpace(gpo.FileSysPath))
        {
            return false;
        }

        var domain = domainDnsName.Trim().TrimEnd('.');
        var expected = @"\\" + domain + @"\SysVol\" + domain + @"\Policies\" + SysvolPaths.FolderName(gpo.Id);
        return !string.Equals(gpo.FileSysPath.Trim().TrimEnd('\\'), expected, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Versions and the folder's presence -----------------------------------------------------------------------------

    private static void VersionAndFolderPresenceRules(Context c)
    {
        foreach (var gpo in c.InScope)
        {
            var folder = c.Evaluates(GpoRuleGroup.Folders) ? c.Snapshot.Folders.GetValueOrDefault(gpo.Id) : null;
            if (folder?.Status == GpoFolderReadStatus.NotFound)
            {
                c.Add(GpoFindingCodes.SysvolFolderMissing, gpo.Id, arguments: ["Folder"]);
                continue;
            }

            if (!c.Evaluates(GpoRuleGroup.SysvolVersions))
            {
                continue;
            }

            if (gpo.SysvolStatus == SysvolFileStatus.NotFound)
            {
                c.Add(GpoFindingCodes.SysvolFolderMissing, gpo.Id, arguments: ["GptIni"]);
            }

            if (gpo.VersionMismatch)
            {
                var directory = gpo.DirectoryVersion!.Value;
                var sysvol = gpo.SysvolVersion!.Value;
                c.Add(GpoFindingCodes.VersionMismatch, gpo.Id,
                    arguments: [Number(directory.User), Number(directory.Computer), Number(sysvol.User), Number(sysvol.Computer)],
                    fix: new GpoQuickFix.AlignVersions(gpo.Id));
            }
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    private static bool UserEnabled(GpoInfo gpo) => gpo.Status is GpoStatus.AllSettingsEnabled or GpoStatus.ComputerSettingsDisabled;

    private static bool ComputerEnabled(GpoInfo gpo) => gpo.Status is GpoStatus.AllSettingsEnabled or GpoStatus.UserSettingsDisabled;

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>What the rules share: the snapshot indexed, the links, the inheritance and the findings so far.</summary>
    private sealed class Context
    {
        private readonly Dictionary<Guid, GpoInfo> _gpos;
        private readonly IReadOnlyDictionary<Guid, IReadOnlyList<GpoLinkInfo>> _links;
        private readonly Dictionary<Guid, GpoFolderView?> _views = [];
        private readonly HashSet<GpoRuleGroup> _available = [GpoRuleGroup.Directory];
        private readonly List<GpoFinding> _findings = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private GroupPolicyInheritance? _inheritance;
        private HashSet<Guid>? _reachingDomainControllers;

        public Context(GpoDiagnosticsSnapshot snapshot)
        {
            Snapshot = snapshot;
            DomainDn = snapshot.DomainDn;
            DomainSid = snapshot.DomainSid;
            _gpos = [];
            foreach (var gpo in snapshot.Gpos)
            {
                _gpos.TryAdd(gpo.Id, gpo);
            }

            _links = GroupPolicyReader.LinksByGpo(snapshot.Containers);
            InScope = snapshot.GpoId is { } id
                ? _gpos.TryGetValue(id, out var one) ? [one] : []
                : [.. _gpos.Values];
            if (snapshot.SysvolVersionsRead)
            {
                _available.Add(GpoRuleGroup.SysvolVersions);
            }

            if (snapshot.Depth == GpoDiagnosticsDepth.Full)
            {
                if (snapshot.FoldersRead)
                {
                    _available.Add(GpoRuleGroup.Folders);
                    _available.Add(GpoRuleGroup.Conflicts);
                }

                if (snapshot.ObjectCounts is not null)
                {
                    _available.Add(GpoRuleGroup.ObjectCounts);
                }

                if (snapshot.ExistingSids is not null && snapshot.DomainSid is not null)
                {
                    _available.Add(GpoRuleGroup.Principals);
                }
            }
        }

        public GpoDiagnosticsSnapshot Snapshot { get; }

        public string DomainDn { get; }

        public string? DomainSid { get; }

        /// <summary>The GPOs the check is about: every GPO of the domain, or the one GPO.</summary>
        public IReadOnlyList<GpoInfo> InScope { get; }

        public bool IsDomainScope => Snapshot.GpoId is null;

        /// <summary>The inheritance of the domain head and the OUs (site links never reach a container's objects alone).</summary>
        public GroupPolicyInheritance Inheritance => _inheritance ??= new GroupPolicyInheritance(DomainDn,
            Snapshot.Containers.Where(x => x.Kind != GroupPolicySomKind.Site).Select(x => x.ToSom()));

        public string DomainControllersDn => Snapshot.DomainControllersDn
            ?? DistinguishedName.Parse(DomainDn).Child("OU", "Domain Controllers").ToString();

        public bool Evaluates(GpoRuleGroup group) => _available.Contains(group);

        public bool InScopeId(Guid id) => Snapshot.GpoId is not { } one || one == id;

        public bool Exists(Guid id) => _gpos.ContainsKey(id);

        public GpoInfo? Gpo(Guid id) => _gpos.GetValueOrDefault(id);

        public IReadOnlyList<GpoLinkInfo> LinksOf(Guid id) => _links.TryGetValue(id, out var links) ? links : [];

        /// <summary>The GPO has an enabled link (domain head, OU or site).</summary>
        public bool IsLinked(Guid id) => LinksOf(id).Any(l => l.Enabled);

        /// <summary>The GPO has an enabled link on the domain head.</summary>
        public bool IsLinkedToDomainHead(Guid id) => LinksOf(id).Any(l => l.Enabled && l.Kind == GroupPolicySomKind.Domain);

        /// <summary>
        /// The GPO reaches the domain controllers: it is in the chain of the Domain Controllers OU (linked there, or to the
        /// domain head and enforced or not blocked).
        /// </summary>
        public bool ReachesDomainControllers(Guid id) =>
            (_reachingDomainControllers ??= [.. Inheritance.Resolve(DomainControllersDn, siteDn: null).Select(a => a.GpoId)]).Contains(id);

        /// <summary>
        /// The version the GPO's clients see: the higher half of AD and GPT.INI (only AD when GPT.INI was not read); null when
        /// neither is known.
        /// </summary>
        public GpoVersion? VersionOf(GpoInfo gpo) =>
            gpo.DirectoryVersion is null && gpo.SysvolVersion is null ? null : gpo.CurrentVersion;

        /// <summary>The parsed folder of a GPO that was read, else null.</summary>
        public GpoFolderView? View(Guid id)
        {
            if (!_views.TryGetValue(id, out var view))
            {
                view = Snapshot.Folders.GetValueOrDefault(id) is { Status: GpoFolderReadStatus.Read, Content: { } content } ? new GpoFolderView(content) : null;
                _views[id] = view;
            }

            return view;
        }

        public void Add(
            string code,
            Guid? gpoId,
            string? containerDn = null,
            string? settingKey = null,
            IReadOnlyList<string>? arguments = null,
            GpoQuickFix? fix = null,
            IReadOnlyList<Guid>? related = null,
            string? gpoName = null)
        {
            var definition = GpoFindingCodes.Find(code);
            var args = arguments ?? [];

            // A rule may meet the same fact twice (a SID listed twice, a key in two files): one finding.
            var identity = string.Join('\u001F', code, gpoId?.ToString() ?? string.Empty, containerDn ?? string.Empty, settingKey ?? string.Empty, string.Join('\u001E', args));
            if (!_seen.Add(identity))
            {
                return;
            }

            _findings.Add(new GpoFinding(code, definition.Severity, definition.Area, gpoId, containerDn, settingKey, args, fix)
            {
                GpoName = gpoName ?? (gpoId is { } id ? Gpo(id)?.DisplayName ?? SysvolPaths.FolderName(id) : null),
                RelatedGpoIds = related ?? [],
            });
        }

        public GpoDiagnosticsResult Result()
        {
            var notEvaluated = Enum.GetValues<GpoRuleGroup>().Where(g => !Evaluates(g)).ToList();
            var notRead = Evaluates(GpoRuleGroup.Folders)
                ? InScope.Where(g => Snapshot.Folders.GetValueOrDefault(g.Id)?.Status is GpoFolderReadStatus.AccessDenied
                    or GpoFolderReadStatus.TooLarge or GpoFolderReadStatus.Failed).Select(g => g.Id).ToList()
                : [];
            var ordered = _findings
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Area)
                .ThenBy(f => f.GpoName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Code, StringComparer.Ordinal)
                .ThenBy(f => f.ContainerDn ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.SettingKey ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => string.Join('\u001E', f.Arguments), StringComparer.Ordinal)
                .ToList();
            return new GpoDiagnosticsResult(ordered, notEvaluated, notRead);
        }
    }
}
