using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Filters;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.PrivilegedAccess;

/// <summary>The privileged access of one account: its findings (level = the highest), in memory only.</summary>
public sealed record AccountPrivileges
{
    public required DirectoryObject Account { get; init; }

    public required PrivilegedAccountType Type { get; init; }

    public required IReadOnlyList<PrivilegeFinding> Findings { get; init; }

    public bool IsDisabled { get; init; }

    /// <summary>The groups the account holds directly: memberOf and the primary group (the peer comparison).</summary>
    public IReadOnlyList<string> DirectGroupDns { get; init; } = [];

    public string DistinguishedName => Account.DistinguishedName;

    /// <summary>The highest level of the findings (<see cref="PrivilegeLevel.None"/> without findings).</summary>
    public PrivilegeLevel Level => PrivilegeLevels.Max(Findings.Select(f => f.Level));

    public bool HasFindings => Findings.Count > 0;

    /// <summary>The sources of the findings.</summary>
    public IReadOnlySet<PrivilegeSource> Sources => Findings.Select(f => f.Source).ToHashSet();

    /// <summary>A peer outlier finding is among the findings.</summary>
    public bool IsPeerOutlier => Findings.Any(f => f.Kind == PrivilegeFindingKind.PeerOutlier);

    /// <summary>The same account with these peer findings instead of the previous ones.</summary>
    public AccountPrivileges WithPeerFindings(IReadOnlyList<PrivilegeFinding> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        return this with { Findings = [.. Findings.Where(f => f.Kind != PrivilegeFindingKind.PeerOutlier), .. peers] };
    }

    /// <summary>The account in the peer comparison.</summary>
    public PeerSubject ToPeerSubject() => new(
        GroupGraph.Key(DistinguishedName),
        Account.Dn.Parent is { IsRoot: false } parent ? parent.ToString() : null,
        Account.GetString(Attr.Department),
        Account.GetString(Attr.Title),
        DirectGroupDns);
}

/// <summary>An account rule with its plan and the accounts the server part of its filter matched (null = all).</summary>
public sealed record AccountRuleMatcher(PrivilegeConditionRule Rule, FilterPlan Plan, IReadOnlySet<string>? Candidates);

/// <summary>
/// Evaluates accounts against the grants of a run: nested membership through the <see cref="GroupGraph"/> (with the
/// shortest path), grants on the account itself, account filter rules and the risky settings of the account (source 2).
/// Thread-safe for concurrent <see cref="Evaluate"/> calls; pure apart from the graph's caches.
/// </summary>
public sealed class PrivilegeEvaluator
{
    /// <summary>The attributes of the account query (rule attributes are added by <see cref="AccountAttributes"/>).</summary>
    public static IReadOnlyList<string> BaseAccountAttributes { get; } =
    [
        Attr.Name, Attr.DisplayName, Attr.SamAccountName, Attr.ObjectClass, Attr.ObjectSid, Attr.UserAccountControl, Attr.MemberOf,
        Attr.PrimaryGroupId, Attr.AdminCount, Attr.ServicePrincipalName, Attr.AllowedToDelegateTo, Attr.AllowedToActOnBehalfOfOtherIdentity,
        Attr.SidHistory, Attr.Department, Attr.Title, Attr.DnsHostName, Attr.Description, Attr.LastLogonTimestamp, Attr.Mail,
    ];

    private readonly List<PrivilegeGrant> _grants = [];
    private readonly Dictionary<int, List<PrivilegeGrant>> _groupGrants = [];
    private readonly Dictionary<string, List<PrivilegeGrant>> _accountGrantsBySid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<PrivilegeGrant>> _accountGrantsByDn = new(StringComparer.Ordinal);
    private readonly List<PrivilegeGrant> _implicitGrants = [];
    private readonly IReadOnlyList<AccountRuleMatcher> _accountRules;
    private readonly HashSet<string> _domainControllerNames;

    /// <param name="groups">The group graph of the run.</param>
    /// <param name="grants">The grants of all sources.</param>
    /// <param name="options">Domain SIDs, included account types.</param>
    /// <param name="filterContext">Context of the account rules' client checks.</param>
    /// <param name="domainControllerNames">Host and NetBIOS names of the DCs (delegation towards a DC is Tier 0).</param>
    /// <param name="accountRules">Account filter rules.</param>
    public PrivilegeEvaluator(
        GroupGraph groups,
        IEnumerable<PrivilegeGrant> grants,
        PrivilegedAccessOptions options,
        FilterContext filterContext,
        IEnumerable<string>? domainControllerNames = null,
        IEnumerable<AccountRuleMatcher>? accountRules = null)
    {
        Groups = groups ?? throw new ArgumentNullException(nameof(groups));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        FilterContext = filterContext ?? throw new ArgumentNullException(nameof(filterContext));
        ArgumentNullException.ThrowIfNull(grants);
        _accountRules = [.. accountRules ?? []];
        _domainControllerNames = new HashSet<string>(domainControllerNames ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var grant in grants)
        {
            _grants.Add(grant);
            var node = groups.FindBySid(grant.PrincipalSid) ?? groups.FindByDn(grant.PrincipalDn);
            if (node is not null)
            {
                Add(_groupGrants, node.Index, grant);
                continue;
            }

            if (grant.PrincipalSid is PrivilegedGroupCatalog.EveryoneSid or PrivilegedGroupCatalog.AuthenticatedUsersSid)
            {
                // Granted to Everyone / Authenticated Users (e.g. by a GPO) while no group of the domain nests them:
                // every account has it.
                _implicitGrants.Add(grant);
                continue;
            }

            if (grant.PrincipalSid is { Length: > 0 } sid)
            {
                Add(_accountGrantsBySid, sid, grant);
            }
            else if (grant.PrincipalDn is { Length: > 0 } dn)
            {
                Add(_accountGrantsByDn, GroupGraph.Key(dn), grant);
            }
        }
    }

    public GroupGraph Groups { get; }

    public PrivilegedAccessOptions Options { get; }

    public FilterContext FilterContext { get; }

    /// <summary>All grants of the run.</summary>
    public IReadOnlyList<PrivilegeGrant> Grants => _grants;

    /// <summary>The attributes of the account query: the base ones and those the account rules check on the client.</summary>
    public IReadOnlyList<string> AccountAttributes => [.. BaseAccountAttributes
        .Concat(_accountRules.SelectMany(r => r.Plan.Attributes))
        .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The level of the grants directly on a group (not inherited).</summary>
    public PrivilegeLevel DirectLevel(GroupNode group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return _groupGrants.TryGetValue(group.Index, out var grants) ? PrivilegeLevels.Max(grants.Select(g => g.Level)) : PrivilegeLevel.None;
    }

    /// <summary>What membership of the group (by DN) grants, nested parents included; None for unknown groups.</summary>
    public PrivilegeLevel GrantedLevel(string groupDn) =>
        Groups.FindByDn(groupDn) is { } node ? Groups.GrantedLevel(node, DirectLevel) : PrivilegeLevel.None;

    /// <summary>Whether the account reaches the group (nested membership, primary group included).</summary>
    public bool IsMemberOf(DirectoryObject account, string groupDn)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (Groups.FindByDn(groupDn) is not { } group)
        {
            return false;
        }

        var self = new PrivilegePathStep(account.DistinguishedName, account.Name, PrivilegePathStepKind.Account);
        return Groups.Reach(self, account.GetStrings(Attr.MemberOf), PrimaryGroupSid(account)).Contains(group);
    }

    /// <summary>The SID of the account's primary group (domain SID + primaryGroupID), null when unknown.</summary>
    public string? PrimaryGroupSid(DirectoryObject account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return Options.DomainSid is { } domainSid && account.GetInt64(Attr.PrimaryGroupId) is { } primary and > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{domainSid}-{primary}")
            : null;
    }

    /// <summary>The name of a group DN (the graph's name, else the RDN value).</summary>
    public string GroupName(string groupDn) =>
        Groups.FindByDn(groupDn)?.Name ?? (DistinguishedName.TryParse(groupDn, out var dn) && !dn.IsRoot ? dn.Name : groupDn);

    /// <summary>The kind of account, or null for objects that are no account (or excluded by the options).</summary>
    public PrivilegedAccountType? TypeOf(DirectoryObject account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var uac = (UserAccountControl)(account.GetInt32(Attr.UserAccountControl) ?? 0);
        PrivilegedAccountType? type = account.Kind switch
        {
            ObjectKind.ManagedServiceAccount or ObjectKind.GroupManagedServiceAccount => PrivilegedAccountType.ServiceAccount,
            ObjectKind.Computer when uac.HasFlag(UserAccountControl.ServerTrustAccount) || uac.HasFlag(UserAccountControl.PartialSecretsAccount) =>
                PrivilegedAccountType.DomainController,
            ObjectKind.Computer => PrivilegedAccountType.Computer,
            ObjectKind.User or ObjectKind.InetOrgPerson => PrivilegedAccountType.User,
            _ => null,
        };
        return type switch
        {
            PrivilegedAccountType.ServiceAccount when !Options.IncludeServiceAccounts => null,
            PrivilegedAccountType.Computer or PrivilegedAccountType.DomainController when !Options.IncludeComputers => null,
            _ => type,
        };
    }

    /// <summary>Evaluates one account (attributes of <see cref="AccountAttributes"/>); null when it is not reviewed.</summary>
    public AccountPrivileges? Evaluate(DirectoryObject account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (TypeOf(account) is not { } type)
        {
            return null;
        }

        var self = new PrivilegePathStep(account.DistinguishedName, account.Name, PrivilegePathStepKind.Account);
        var memberOf = account.GetStrings(Attr.MemberOf);
        var paths = Groups.Reach(self, memberOf, PrimaryGroupSid(account));
        var findings = new List<PrivilegeFinding>();

        // Grants on the groups the account reaches, each with the shortest path.
        foreach (var group in paths.Groups)
        {
            if (!_groupGrants.TryGetValue(group.Index, out var grants))
            {
                continue;
            }

            var path = paths.PathTo(group);
            findings.AddRange(grants.Select(g => FromGrant(g, path, group.Dn, group.Name, onAccount: false)));
        }

        // Grants on Everyone / Authenticated Users that no group of the graph nests.
        foreach (var grant in _implicitGrants)
        {
            var step = new PrivilegePathStep($"<SID={grant.PrincipalSid}>", grant.PrincipalSid!, PrivilegePathStepKind.ImplicitGroup);
            findings.Add(FromGrant(grant, [self, step], step.Dn, step.Name, onAccount: false));
        }

        // Grants on the account itself.
        var sid = account.ObjectSid;
        var own = new List<PrivilegeGrant>();
        if (sid is not null && _accountGrantsBySid.TryGetValue(sid, out var bySid))
        {
            own.AddRange(bySid);
        }

        if (_accountGrantsByDn.TryGetValue(GroupGraph.Key(account.DistinguishedName), out var byDn))
        {
            own.AddRange(byDn.Where(g => !own.Contains(g)));
        }

        findings.AddRange(own.Select(g => FromGrant(g, [self], account.DistinguishedName, account.Name, onAccount: true)));

        // Account filter rules ("mark as privileged with level X").
        var key = GroupGraph.Key(account.DistinguishedName);
        foreach (var rule in _accountRules)
        {
            if (rule.Candidates is { } candidates && !candidates.Contains(key))
            {
                continue;
            }

            if (rule.Plan.NeedsClientCheck && !FilterEvaluator.Matches(rule.Rule.Filter, account, FilterContext, dn => Groups.FindByDn(dn) is { } g && paths.Contains(g)))
            {
                continue;
            }

            findings.Add(new PrivilegeFinding
            {
                Source = PrivilegeSource.OperatorRules,
                Kind = PrivilegeFindingKind.ConditionAccount,
                Level = PrivilegeLevels.Normalize(rule.Rule.Level),
                Scope = PrivilegeScopeKind.Domain,
                SubjectDn = account.DistinguishedName,
                SubjectName = account.Name,
                Path = [self],
                Note = rule.Rule.Note is { Length: > 0 } note ? $"{rule.Rule.Name} – {note}" : rule.Rule.Name,
            });
        }

        findings.AddRange(AccountSettings(account, type, self, paths));
        var direct = paths.DirectGroups.Select(g => g.Dn).ToList();
        return new AccountPrivileges
        {
            Account = account,
            Type = type,
            Findings = Order(findings),
            IsDisabled = account.IsDisabled,
            DirectGroupDns = direct,
        };
    }

    /// <summary>Findings ordered by level (highest first), then source and kind.</summary>
    public static IReadOnlyList<PrivilegeFinding> Order(IEnumerable<PrivilegeFinding> findings) =>
        [.. findings.OrderByDescending(f => f.Level).ThenBy(f => f.Source).ThenBy(f => f.Kind).ThenBy(f => f.Path.Count)
            .ThenBy(f => f.SubjectName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Source 2: the settings of the account itself.</summary>
    private IEnumerable<PrivilegeFinding> AccountSettings(DirectoryObject account, PrivilegedAccountType type, PrivilegePathStep self, MembershipPaths paths)
    {
        var uac = (UserAccountControl)(account.GetInt32(Attr.UserAccountControl) ?? 0);
        var rid = AdConvert.GetRid(account.ObjectSid);
        var isOwnDomain = Options.DomainSid is { } domainSid && account.ObjectSid is { } accountSid
            && accountSid.StartsWith(domainSid + "-", StringComparison.OrdinalIgnoreCase);
        var wellKnown = isOwnDomain && rid is WellKnownRids.Administrator or WellKnownRids.Krbtgt;

        PrivilegeFinding Finding(PrivilegeFindingKind kind, PrivilegeLevel level, PrivilegeScopeKind scope, IReadOnlyList<string>? values = null) => new()
        {
            Source = PrivilegeSource.AccountSettings,
            Kind = kind,
            Level = level,
            Scope = scope,
            SubjectDn = account.DistinguishedName,
            SubjectName = account.Name,
            Path = [self],
            Values = values ?? [],
        };

        if (wellKnown)
        {
            yield return Finding(PrivilegeFindingKind.WellKnownAccount, PrivilegeLevel.Tier0, PrivilegeScopeKind.Domain) with
            {
                Values = [rid == WellKnownRids.Krbtgt ? "krbtgt" : "Administrator"],
            };
        }

        if (account.GetInt32(Attr.AdminCount) == 1)
        {
            var protectedMembership = wellKnown || paths.Groups.Any(g =>
                PrivilegedGroupCatalog.Match(g.Sid, g.SamAccountName, Options.DomainSid, Options.RootDomainSid)?.IsProtected == true);
            yield return Finding(protectedMembership ? PrivilegeFindingKind.AdminCount : PrivilegeFindingKind.OrphanedAdminCount, PrivilegeLevel.Minor, PrivilegeScopeKind.Account);
        }

        var spns = account.GetStrings(Attr.ServicePrincipalName);
        if (type == PrivilegedAccountType.User && spns.Count > 0)
        {
            yield return Finding(PrivilegeFindingKind.Kerberoastable, PrivilegeLevel.Elevated, PrivilegeScopeKind.Account, spns);
        }

        if (uac.HasFlag(UserAccountControl.TrustedForDelegation) && type != PrivilegedAccountType.DomainController)
        {
            yield return Finding(PrivilegeFindingKind.UnconstrainedDelegation, PrivilegeLevel.Tier0, PrivilegeScopeKind.Domain);
        }

        var targets = account.GetStrings(Attr.AllowedToDelegateTo);
        if (targets.Count > 0)
        {
            var towardsDc = targets.Any(t => _domainControllerNames.Contains(HostOf(t)));
            var transition = uac.HasFlag(UserAccountControl.TrustedToAuthForDelegation);
            yield return Finding(
                transition ? PrivilegeFindingKind.ProtocolTransition : PrivilegeFindingKind.ConstrainedDelegation,
                towardsDc ? PrivilegeLevel.Tier0 : PrivilegeLevel.High,
                PrivilegeScopeKind.Domain,
                targets);
        }

        if (account.Has(Attr.AllowedToActOnBehalfOfOtherIdentity) && account.GetBytes(Attr.AllowedToActOnBehalfOfOtherIdentity) is { Length: > 0 })
        {
            yield return Finding(
                PrivilegeFindingKind.ResourceBasedDelegation,
                type == PrivilegedAccountType.DomainController ? PrivilegeLevel.Tier0 : PrivilegeLevel.Elevated,
                PrivilegeScopeKind.Account);
        }

        if (account.TryGet(Attr.SidHistory, out var history) && history.Count > 0)
        {
            var sids = history.Select(v => v is byte[] bytes && bytes.Length >= 8 ? AdConvert.SidToString(bytes) : v.ToString() ?? string.Empty)
                .Where(s => s.Length > 0)
                .ToList();
            var level = PrivilegeLevel.Elevated;
            foreach (var sid in sids)
            {
                if (Options.DomainSid is { } own && sid.StartsWith(own + "-", StringComparison.OrdinalIgnoreCase))
                {
                    level = PrivilegeLevels.Max(level, PrivilegeLevel.High);
                }

                if (PrivilegedGroupCatalog.Match(sid, null, Options.DomainSid, Options.RootDomainSid) is { } group)
                {
                    level = PrivilegeLevels.Max(level, Options.LevelOf(group));
                }
                else if (Groups.FindBySid(sid) is { } node)
                {
                    level = PrivilegeLevels.Max(level, Groups.GrantedLevel(node, DirectLevel));
                }
            }

            yield return Finding(PrivilegeFindingKind.SidHistory, level, PrivilegeScopeKind.Domain, sids);
        }

        if (uac.HasFlag(UserAccountControl.DontRequirePreauth))
        {
            yield return Finding(PrivilegeFindingKind.PreauthNotRequired, PrivilegeLevel.Elevated, PrivilegeScopeKind.Account);
        }
    }

    private static PrivilegeFinding FromGrant(PrivilegeGrant grant, IReadOnlyList<PrivilegePathStep> path, string subjectDn, string subjectName, bool onAccount) => new()
    {
        Source = grant.Source,
        Kind = onAccount ? grant.Kind switch
        {
            PrivilegeFindingKind.MarkedGroup => PrivilegeFindingKind.MarkedAccount,
            PrivilegeFindingKind.ConditionGroup => PrivilegeFindingKind.ConditionAccount,
            var kind => kind,
        } : grant.Kind,
        Level = grant.Level,
        Scope = grant.Scope,
        ScopeDn = grant.ScopeDn,
        SubjectDn = subjectDn,
        SubjectName = subjectName,
        CatalogId = grant.CatalogId,
        Path = path,
        Values = grant.Values,
        Note = grant.Note,
        GroupPolicy = grant.GroupPolicy,
    };

    /// <summary>The host of a service principal name (service/host[:port][/name]).</summary>
    internal static string HostOf(string spn)
    {
        var slash = spn.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return spn;
        }

        var host = spn[(slash + 1)..];
        var end = host.IndexOfAny([':', '/']);
        return end < 0 ? host : host[..end];
    }

    private static void Add<TKey>(Dictionary<TKey, List<PrivilegeGrant>> map, TKey key, PrivilegeGrant grant)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(grant);
    }
}
