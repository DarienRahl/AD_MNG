using System.Globalization;
using AdMng.Core.GroupPolicy;
using AdMng.Core.Ldap;

namespace AdMng.Core.PrivilegedAccess;

/// <summary>The settings of one GPO that grant local rights (from SYSVOL, in memory only).</summary>
/// <param name="Template">GptTmpl.inf (Restricted Groups, User Rights Assignment), null when the GPO has none.</param>
/// <param name="LocalGroups">GPP Groups.xml local group items, empty when the GPO has none.</param>
public sealed record GroupPolicyRightSettings(SecurityTemplate? Template, IReadOnlyList<GppLocalGroup> LocalGroups)
{
    public static GroupPolicyRightSettings Empty { get; } = new(null, []);
}

/// <summary>The domain's names a GPO may use for its principals (<c>CONTOSO\GG_Serwis</c>, <c>contoso.test\GG_Serwis</c>).</summary>
/// <param name="DnsName">DNS name of the domain.</param>
/// <param name="NetBiosName">NetBIOS name, null when unknown.</param>
public sealed record GroupPolicyDomainNames(string DnsName, string? NetBiosName)
{
    /// <summary>The domain part of <c>DOMAIN\name</c> names this domain.</summary>
    public bool IsThisDomain(string domain) =>
        domain.Equals(DnsName, StringComparison.OrdinalIgnoreCase)
        || (NetBiosName is not null && domain.Equals(NetBiosName, StringComparison.OrdinalIgnoreCase))
        || domain.Equals(DnsName.Split('.')[0], StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Turns the GPOs that apply to the computers of a domain into grants of local rights (U4c, design
/// docs/dev/u4-privileged-access.md section 6). Pure: the caller reads the topology (LDAP), the GPO files (SYSVOL) and
/// the SIDs of the names (LDAP) first.
/// <list type="bullet">
/// <item>The computers are grouped by their parent container (and the site of a DC); for each group
/// <see cref="GroupPolicyInheritance"/> gives the GPOs in application order (GPOs with the computer configuration disabled
/// left out).</item>
/// <item>User Rights Assignment and Restricted Groups follow the security engine: per right, per restricted group (Members)
/// and per member (MemberOf) the highest-precedence GPO wins; the effective members of a local group are the Members list
/// plus every MemberOf entry naming it.</item>
/// <item>GPP local groups are applied item by item in that order: ADD/REMOVE, "delete all member users/groups", Replace and
/// Delete remove what earlier items added. Items with item-level targeting are kept but marked conditional; their removals
/// are not applied (they may not match). Restricted Groups and GPP are evaluated independently (both are reported).</item>
/// <item>Principals: SIDs of the domain and Everyone / Authenticated Users / Users; names of the domain through their
/// SIDs; local groups, service SIDs, other domains and Tier 0 groups of the catalogue (already Tier 0) are not repeated.</item>
/// <item>One grant per principal, right, mechanism, GPO and link, with the computers it reaches by role and the level of
/// the highest role (<see cref="LocalRightsCatalog.LevelOn"/>); a right that reaches no reported role is dropped.</item>
/// </list>
/// </summary>
public sealed class GroupPolicyRightsResolver
{
    private readonly GroupPolicyTopology _topology;
    private readonly IReadOnlyDictionary<Guid, GroupPolicyRightSettings> _settings;
    private readonly IReadOnlyDictionary<string, string> _sidsByName;
    private readonly GroupGraph _groups;
    private readonly PrivilegedAccessOptions _options;
    private readonly GroupPolicyDomainNames _domain;
    private readonly Dictionary<string, string> _builtinNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unresolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _missingPolicies = [];

    /// <param name="topology">GPOs, links and computers.</param>
    /// <param name="settings">The parsed files per GPO (a GPO without an entry has no settings).</param>
    /// <param name="sidsByName">SIDs of the names the GPOs use (sAMAccountName → SID, case-insensitive).</param>
    /// <param name="groups">The group graph of the run (groups vs users, built-in group names, catalogue matches).</param>
    /// <param name="options">Domain SIDs and the operator's levels.</param>
    /// <param name="domain">The domain's names.</param>
    public GroupPolicyRightsResolver(
        GroupPolicyTopology topology,
        IReadOnlyDictionary<Guid, GroupPolicyRightSettings> settings,
        IReadOnlyDictionary<string, string> sidsByName,
        GroupGraph groups,
        PrivilegedAccessOptions options,
        GroupPolicyDomainNames domain)
    {
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sidsByName = sidsByName ?? throw new ArgumentNullException(nameof(sidsByName));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _domain = domain ?? throw new ArgumentNullException(nameof(domain));
        foreach (var node in groups.Nodes.Where(n => n.Sid is { } sid && sid.StartsWith("S-1-5-32-", StringComparison.Ordinal) && n.SamAccountName is not null))
        {
            _builtinNames.TryAdd(node.SamAccountName!, node.Sid!);
        }
    }

    /// <summary>Names (as written, without SID) the GPOs use; the domain ones need their SID from the directory.</summary>
    public IReadOnlyList<string> UnresolvedNames => [.. _unresolved.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>GPOs that are linked and would apply but have no group policy container in this domain.</summary>
    public int MissingPolicies => _missingPolicies.Count;

    /// <summary>
    /// The sAMAccountNames the settings name (<c>CONTOSO\GG_Serwis</c> → <c>GG_Serwis</c>) - what the caller resolves to
    /// SIDs before <see cref="Resolve"/>. Names of other domains, BUILTIN and NT AUTHORITY are left out.
    /// </summary>
    public static IReadOnlyList<string> NamesToResolve(IEnumerable<GroupPolicyRightSettings> settings, GroupPolicyDomainNames domain)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(domain);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gpo in settings)
        {
            var principals = new List<string>();
            if (gpo.Template is { } template)
            {
                principals.AddRange(template.PrivilegeRights.SelectMany(r => r.Principals));
                principals.AddRange(template.GroupMembership.SelectMany(g => g.Principals));
                principals.AddRange(template.GroupMembership.Where(g => g.Relation == RestrictedGroupRelation.MemberOf).Select(g => g.Group));
            }

            principals.AddRange(gpo.LocalGroups.SelectMany(g => g.Members).Where(m => m.Sid is null && m.Name is not null).Select(m => m.Name!));
            foreach (var principal in principals)
            {
                if (!SecurityTemplate.IsSid(principal) && AccountName(principal, domain) is { } name)
                {
                    names.Add(name);
                }
            }
        }

        return [.. names.Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The grants of local rights.</summary>
    public IReadOnlyList<PrivilegeGrant> Resolve()
    {
        _unresolved.Clear();
        _missingPolicies.Clear();
        var inheritance = new GroupPolicyInheritance(_topology.DomainDn, _topology.Containers);
        var totals = new Dictionary<GrantKey, Accumulator>();
        var groups = _topology.Computers
            .GroupBy(c => (Parent: ParentKey(c.Dn), Site: c.SiteDn is null ? null : DistinguishedName.TryParse(c.SiteDn, out var s) ? s.ToString().ToUpperInvariant() : null))
            .ToList();
        foreach (var group in groups)
        {
            var first = group.First();
            var parentDn = DistinguishedName.TryParse(first.Dn, out var dn) && dn.Parent is { } parent ? parent.ToString() : _topology.DomainDn;
            var applied = inheritance.Resolve(parentDn, first.SiteDn)
                .Where(a =>
                {
                    if (!_topology.Policies.TryGetValue(a.GpoId, out var policy))
                    {
                        _missingPolicies.Add(a.GpoId);
                        return false;
                    }

                    return !policy.ComputerSettingsDisabled;
                })
                .ToList();
            if (applied.Count == 0)
            {
                continue;
            }

            var counts = new RoleCounts(
                group.Count(c => c.Role == ComputerRole.DomainController),
                group.Count(c => c.Role == ComputerRole.Server),
                group.Count(c => c.Role == ComputerRole.Workstation));
            foreach (var raw in Evaluate(applied))
            {
                var key = new GrantKey(raw.PrincipalSid, raw.Definition.Key, raw.Mechanism, raw.Applied.GpoId, ScopeKey(raw.Applied.Container.Dn));
                if (!totals.TryGetValue(key, out var total))
                {
                    totals[key] = total = new Accumulator(raw.Applied, raw.Definition, raw.ItemLevelTargeting);
                }

                total.Add(counts, raw.ItemLevelTargeting);
            }
        }

        var grants = new List<PrivilegeGrant>();
        foreach (var (key, total) in totals)
        {
            var policy = _topology.Policies[key.GpoId];
            var level = PrivilegeLevels.Max(
            [
                total.Counts.DomainControllers > 0 ? LocalRightsCatalog.LevelOn(total.Definition, ComputerRole.DomainController, _options) : PrivilegeLevel.None,
                total.Counts.Servers > 0 ? LocalRightsCatalog.LevelOn(total.Definition, ComputerRole.Server, _options) : PrivilegeLevel.None,
                total.Counts.Workstations > 0 ? LocalRightsCatalog.LevelOn(total.Definition, ComputerRole.Workstation, _options) : PrivilegeLevel.None,
            ]);
            if (level == PrivilegeLevel.None)
            {
                continue;
            }

            grants.Add(new PrivilegeGrant
            {
                Source = PrivilegeSource.GroupPolicy,
                Kind = PrivilegeFindingKind.LocalRight,
                Level = level,
                Scope = PrivilegeScopeKind.Computers,
                ScopeDn = total.Applied.Container.Dn,
                PrincipalSid = key.PrincipalSid,
                Values = [policy.DisplayName, total.Definition.Key],
                GroupPolicy = new GroupPolicyGrantDetails
                {
                    GpoId = policy.Id,
                    GpoName = policy.DisplayName,
                    GpoDn = policy.Dn,
                    Mechanism = key.Mechanism,
                    Right = total.Definition.Key,
                    Definition = total.Definition,
                    LinkDn = total.Applied.Container.Dn,
                    LinkEnforced = total.Applied.Link.Enforced,
                    LinkOrder = total.Applied.Link.Order,
                    ItemLevelTargeting = total.AllItemLevelTargeting,
                    WmiFilter = policy.HasWmiFilter,
                    DomainControllers = total.Counts.DomainControllers,
                    Servers = total.Counts.Servers,
                    Workstations = total.Counts.Workstations,
                },
            });
        }

        return [.. grants
            .OrderByDescending(g => g.Level)
            .ThenBy(g => g.GroupPolicy!.GpoName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.GroupPolicy!.Right, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.PrincipalSid, StringComparer.Ordinal)];
    }

    /// <summary>The effective local rights of computers that get <paramref name="applied"/> (in application order).</summary>
    private List<RawGrant> Evaluate(IReadOnlyList<AppliedGroupPolicy> applied)
    {
        var rights = new Dictionary<string, (AppliedGroupPolicy Applied, IReadOnlyList<string> Principals)>(StringComparer.OrdinalIgnoreCase);
        var members = new Dictionary<string, (AppliedGroupPolicy Applied, IReadOnlyList<string> Principals)>(StringComparer.OrdinalIgnoreCase);
        var memberOf = new Dictionary<string, (AppliedGroupPolicy Applied, string Principal, IReadOnlyList<string> Groups)>(StringComparer.OrdinalIgnoreCase);
        var preferences = new Dictionary<string, Dictionary<string, (AppliedGroupPolicy Applied, bool Targeted)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var gpo in applied)
        {
            if (!_settings.TryGetValue(gpo.GpoId, out var settings))
            {
                continue;
            }

            if (settings.Template is { } template)
            {
                foreach (var right in template.PrivilegeRights)
                {
                    rights[right.Right] = (gpo, right.Principals);
                }

                foreach (var entry in template.GroupMembership)
                {
                    if (entry.Relation == RestrictedGroupRelation.Members)
                    {
                        // Members of a local group; a domain group on the left is a domain membership, not a local right.
                        if (LocalGroupSid(entry.Group) is { } localGroup)
                        {
                            members[localGroup] = (gpo, entry.Principals);
                        }
                    }
                    else
                    {
                        memberOf[PrincipalSid(entry.Group) ?? entry.Group] = (gpo, entry.Group, entry.Principals);
                    }
                }
            }

            foreach (var item in settings.LocalGroups)
            {
                if (item.Disabled || (item.GroupSid ?? LocalGroupSid(item.GroupName)) is not { } localGroup || !localGroup.StartsWith("S-1-5-32-", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!preferences.TryGetValue(localGroup, out var current))
                {
                    preferences[localGroup] = current = new Dictionary<string, (AppliedGroupPolicy, bool)>(StringComparer.OrdinalIgnoreCase);
                }

                var targeted = item.HasItemLevelTargeting;
                if (!targeted)
                {
                    if (item.Action is GppItemAction.Delete or GppItemAction.Replace)
                    {
                        current.Clear();
                    }

                    if (item.DeleteAllUsers)
                    {
                        RemoveWhere(current, sid => _groups.FindBySid(sid) is null && !IsImplicit(sid));
                    }

                    if (item.DeleteAllGroups)
                    {
                        RemoveWhere(current, sid => _groups.FindBySid(sid) is not null || IsImplicit(sid));
                    }
                }

                if (item.Action == GppItemAction.Delete)
                {
                    continue;
                }

                foreach (var member in item.Members)
                {
                    var sid = member.Sid ?? (member.Name is { } name ? PrincipalSid(name) : null);
                    if (sid is null)
                    {
                        continue;
                    }

                    if (member.Action == GppMemberAction.Add)
                    {
                        current[sid] = (gpo, targeted);
                    }
                    else if (!targeted)
                    {
                        current.Remove(sid);
                    }
                }
            }
        }

        var result = new List<RawGrant>();
        foreach (var (right, (gpo, principals)) in rights)
        {
            if (LocalRightsCatalog.Find(right) is { Kind: LocalRightKind.UserRight } definition)
            {
                AddPrincipals(result, principals, definition, LocalRightMechanism.UserRightsAssignment, gpo, targeted: false);
            }
        }

        foreach (var (localGroup, (gpo, principals)) in members)
        {
            if (LocalRightsCatalog.Find(localGroup) is { Kind: LocalRightKind.LocalGroup } definition)
            {
                AddPrincipals(result, principals, definition, LocalRightMechanism.RestrictedGroupMembers, gpo, targeted: false);
            }
        }

        foreach (var (_, (gpo, principal, localGroups)) in memberOf)
        {
            foreach (var localGroup in localGroups)
            {
                if (LocalGroupSid(localGroup) is { } sid && LocalRightsCatalog.Find(sid) is { Kind: LocalRightKind.LocalGroup } definition)
                {
                    AddPrincipals(result, [principal], definition, LocalRightMechanism.RestrictedGroupMemberOf, gpo, targeted: false);
                }
            }
        }

        foreach (var (localGroup, current) in preferences)
        {
            if (LocalRightsCatalog.Find(localGroup) is not { Kind: LocalRightKind.LocalGroup } definition)
            {
                continue;
            }

            foreach (var (sid, (gpo, targeted)) in current)
            {
                AddPrincipals(result, [sid], definition, LocalRightMechanism.PreferenceGroups, gpo, targeted);
            }
        }

        return result;
    }

    private void AddPrincipals(List<RawGrant> result, IEnumerable<string> principals, LocalRightDefinition definition, LocalRightMechanism mechanism,
        AppliedGroupPolicy gpo, bool targeted)
    {
        foreach (var principal in principals)
        {
            if (PrincipalSid(principal) is { } sid && IsReported(sid))
            {
                result.Add(new RawGrant(sid, definition, mechanism, gpo, targeted));
            }
        }
    }

    /// <summary>
    /// Whether a holder is worth a finding: a principal of this domain (or Everyone / Authenticated Users / Users) that is not
    /// already Tier 0 through the catalogue; local groups, service SIDs and other domains are not accounts of the domain.
    /// </summary>
    private bool IsReported(string sid)
    {
        if (IsImplicit(sid) || sid.Equals(LocalRightsCatalog.UsersSid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = sid[..sid.LastIndexOf('-')];
        var ownDomain = _options.DomainSid is null || prefix.Equals(_options.DomainSid, StringComparison.OrdinalIgnoreCase);
        var group = PrivilegedGroupCatalog.Match(sid, _groups.FindBySid(sid)?.SamAccountName, _options.DomainSid, _options.RootDomainSid);
        if (group is not null && _options.LevelOf(group) == PrivilegeLevel.Tier0)
        {
            return false;
        }

        return ownDomain;
    }

    private static bool IsImplicit(string sid) =>
        sid.Equals(PrivilegedGroupCatalog.EveryoneSid, StringComparison.Ordinal) || sid.Equals(PrivilegedGroupCatalog.AuthenticatedUsersSid, StringComparison.Ordinal);

    /// <summary>The SID of a principal as a GPO writes it (SID, <c>DOMAIN\name</c>, <c>name</c>), null when unknown.</summary>
    private string? PrincipalSid(string principal)
    {
        var text = SecurityTemplate.Principal(principal);
        if (SecurityTemplate.IsSid(text))
        {
            return text;
        }

        if (WellKnownName(text) is { } wellKnown)
        {
            return wellKnown;
        }

        if (AccountName(text, _domain) is { } name)
        {
            if (_sidsByName.TryGetValue(name, out var sid))
            {
                return sid;
            }

            if (LocalGroupSid(name) is { } local)
            {
                return local;
            }
        }
        else if (text.StartsWith("BUILTIN\\", StringComparison.OrdinalIgnoreCase) && LocalGroupSid(text) is { } builtin)
        {
            return builtin;
        }

        _unresolved.Add(text);
        return null;
    }

    private string? LocalGroupSid(string? nameOrSid) =>
        LocalRightsCatalog.LocalGroupSid(nameOrSid, name => _builtinNames.TryGetValue(name, out var sid) ? sid : null);

    /// <summary>Everyone and Authenticated Users by name (English and Polish, with or without NT AUTHORITY\).</summary>
    private static string? WellKnownName(string text)
    {
        var name = text[(text.LastIndexOf('\\') + 1)..].Trim();
        return name.ToUpperInvariant() switch
        {
            "EVERYONE" or "WSZYSCY" => PrivilegedGroupCatalog.EveryoneSid,
            "AUTHENTICATED USERS" or "UŻYTKOWNICY UWIERZYTELNIENI" => PrivilegedGroupCatalog.AuthenticatedUsersSid,
            _ => null,
        };
    }

    /// <summary>The sAMAccountName of a name of this domain (<c>CONTOSO\x</c>, <c>x@contoso.test</c>, <c>x</c>), else null.</summary>
    private static string? AccountName(string principal, GroupPolicyDomainNames domain)
    {
        var text = principal.Trim();
        var backslash = text.IndexOf('\\', StringComparison.Ordinal);
        if (backslash >= 0)
        {
            var prefix = text[..backslash];
            return domain.IsThisDomain(prefix) && text.Length > backslash + 1 ? text[(backslash + 1)..] : null;
        }

        var at = text.LastIndexOf('@');
        if (at > 0)
        {
            return domain.IsThisDomain(text[(at + 1)..]) ? text[..at] : null;
        }

        return text.Length > 0 ? text : null;
    }

    private static void RemoveWhere(Dictionary<string, (AppliedGroupPolicy, bool)> current, Func<string, bool> predicate)
    {
        foreach (var sid in current.Keys.Where(predicate).ToList())
        {
            current.Remove(sid);
        }
    }

    private static string ParentKey(string dn) =>
        DistinguishedName.TryParse(dn, out var parsed) && parsed.Parent is { } parent ? parent.ToString().ToUpperInvariant() : string.Empty;

    private static string ScopeKey(string dn) =>
        (DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToString() : dn).ToUpperInvariant();

    private readonly record struct GrantKey(string PrincipalSid, string Right, LocalRightMechanism Mechanism, Guid GpoId, string Scope);

    private readonly record struct RawGrant(string PrincipalSid, LocalRightDefinition Definition, LocalRightMechanism Mechanism, AppliedGroupPolicy Applied, bool ItemLevelTargeting);

    private readonly record struct RoleCounts(int DomainControllers, int Servers, int Workstations)
    {
        public RoleCounts Plus(RoleCounts other) =>
            new(DomainControllers + other.DomainControllers, Servers + other.Servers, Workstations + other.Workstations);
    }

    private sealed class Accumulator(AppliedGroupPolicy applied, LocalRightDefinition definition, bool targeted)
    {
        public AppliedGroupPolicy Applied { get; } = applied;

        public LocalRightDefinition Definition { get; } = definition;

        public RoleCounts Counts { get; private set; }

        public bool AllItemLevelTargeting { get; private set; } = targeted;

        public void Add(RoleCounts counts, bool targeted)
        {
            Counts = Counts.Plus(counts);
            AllItemLevelTargeting &= targeted;
        }
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"GroupPolicyRightsResolver ({_topology})");
}
