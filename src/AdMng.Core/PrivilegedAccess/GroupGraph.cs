using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.PrivilegedAccess;

/// <summary>A security group (or an implicit principal such as Authenticated Users) of the <see cref="GroupGraph"/>.</summary>
public sealed class GroupNode
{
    private readonly List<GroupNode> _parents = [];

    internal GroupNode(int index, DirectoryObject obj, bool isImplicit)
    {
        Index = index;
        Object = obj;
        Dn = obj.DistinguishedName;
        Sid = obj.ObjectSid;
        SamAccountName = obj.GetString(Attr.SamAccountName);
        Name = obj.Name;
        IsImplicit = isImplicit;
    }

    internal int Index { get; }

    /// <summary>The group as read (attributes of <see cref="GroupGraph.Attributes"/> and the extra ones asked for).</summary>
    public DirectoryObject Object { get; }

    public string Dn { get; }

    public string? Sid { get; }

    public string? SamAccountName { get; }

    public string Name { get; }

    /// <summary>A foreign security principal every account is a member of (Everyone, Authenticated Users).</summary>
    public bool IsImplicit { get; }

    /// <summary>The groups this one is a direct member of (memberOf, security groups of the domain only).</summary>
    public IReadOnlyList<GroupNode> Parents => _parents;

    internal void AddParent(GroupNode parent)
    {
        if (!ReferenceEquals(parent, this) && !_parents.Contains(parent))
        {
            _parents.Add(parent);
        }
    }

    public override string ToString() => Dn;
}

/// <summary>
/// The security groups of the domain with their memberOf links, read once per run (one paged search) and kept in memory.
/// Nested membership of an account is a breadth-first search upwards from its direct groups, its primary group and the
/// implicit principals - shortest paths, cycles handled, no per-account query (design docs/dev/u4-privileged-access.md).
/// Distribution groups are left out: membership in them never reaches a security group's token.
/// </summary>
public sealed class GroupGraph
{
    /// <summary>The attributes every node is read with.</summary>
    public static IReadOnlyList<string> Attributes { get; } =
        [Attr.Name, Attr.SamAccountName, Attr.ObjectSid, Attr.MemberOf, Attr.GroupType, Attr.AdminCount, Attr.ObjectClass];

    /// <summary>Security groups and the foreign security principals that are members of something.</summary>
    public static string Filter { get; } = LdapFilter.Or(
        LdapFilter.And(ObjectKind.Group.ToLdapFilter(), LdapFilter.BitAnd(Attr.GroupType, (long)GroupTypeFlags.Security)),
        LdapFilter.And(ObjectKind.ForeignSecurityPrincipal.ToLdapFilter(), LdapFilter.Present(Attr.MemberOf)));

    private readonly List<GroupNode> _nodes = [];
    private readonly Dictionary<string, GroupNode> _byDn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupNode> _bySid = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GroupNode> _implicit = [];
    private readonly Dictionary<int, PrivilegeLevel> _grantedLevelCache = [];
    private readonly Lock _gate = new();

    /// <param name="objects">Security groups and foreign security principals (others are ignored).</param>
    public GroupGraph(IEnumerable<DirectoryObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        foreach (var obj in objects)
        {
            var kind = obj.Kind;
            var isFsp = kind == ObjectKind.ForeignSecurityPrincipal;
            if (kind != ObjectKind.Group && !isFsp)
            {
                continue;
            }

            if (isFsp && obj.ObjectSid is not (PrivilegedGroupCatalog.EveryoneSid or PrivilegedGroupCatalog.AuthenticatedUsersSid))
            {
                // Principals of trusted domains and other well-known SIDs are not accounts of this domain.
                continue;
            }

            var key = Key(obj.DistinguishedName);
            if (_byDn.ContainsKey(key))
            {
                continue;
            }

            var node = new GroupNode(_nodes.Count, obj, isFsp);
            _nodes.Add(node);
            _byDn[key] = node;
            if (node.Sid is { } sid)
            {
                _bySid.TryAdd(sid, node);
            }

            if (isFsp)
            {
                _implicit.Add(node);
            }
        }

        foreach (var node in _nodes)
        {
            foreach (var parent in node.Object.GetStrings(Attr.MemberOf))
            {
                if (_byDn.TryGetValue(Key(parent), out var target))
                {
                    node.AddParent(target);
                }
            }
        }
    }

    public IReadOnlyList<GroupNode> Nodes => _nodes;

    /// <summary>Reads the graph: one paged subtree search of the default naming context.</summary>
    /// <param name="session">The session (all reads on its DC, CLAUDE.md rule 4).</param>
    /// <param name="extraAttributes">More attributes for the client checks of group rules.</param>
    /// <param name="pageSize">LDAP page size.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<GroupGraph> ReadAsync(
        IDirectorySession session,
        IEnumerable<string>? extraAttributes,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = Filter,
            Attributes = [.. Attributes.Concat(extraAttributes ?? []).Distinct(StringComparer.OrdinalIgnoreCase)],
            PageSize = pageSize,
        };
        var objects = new List<DirectoryObject>();
        await foreach (var obj in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            objects.Add(obj);
        }

        return new GroupGraph(objects);
    }

    public GroupNode? FindByDn(string? dn) => dn is not null && _byDn.TryGetValue(Key(dn), out var node) ? node : null;

    public GroupNode? FindBySid(string? sid) => sid is not null && _bySid.TryGetValue(sid, out var node) ? node : null;

    /// <summary>
    /// Every group the account reaches (nested), each with the shortest path from the account: breadth-first from the
    /// direct groups (memberOf), the primary group and the implicit principals.
    /// </summary>
    /// <param name="account">The account step the paths start with.</param>
    /// <param name="memberOf">The account's memberOf values.</param>
    /// <param name="primaryGroupSid">SID of the primary group (domain SID + primaryGroupID), null when unknown.</param>
    /// <param name="includeImplicit">Start from Everyone / Authenticated Users too (accounts: yes; groups: no).</param>
    public MembershipPaths Reach(PrivilegePathStep account, IEnumerable<string> memberOf, string? primaryGroupSid, bool includeImplicit = true)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(memberOf);
        var predecessor = new Dictionary<int, (int From, PrivilegePathStepKind Kind)>();
        var queue = new Queue<GroupNode>();

        void Start(GroupNode? node, PrivilegePathStepKind kind)
        {
            if (node is not null && predecessor.TryAdd(node.Index, (-1, kind)))
            {
                queue.Enqueue(node);
            }
        }

        foreach (var dn in memberOf)
        {
            Start(FindByDn(dn), PrivilegePathStepKind.Group);
        }

        Start(FindBySid(primaryGroupSid), PrivilegePathStepKind.PrimaryGroup);
        foreach (var node in includeImplicit ? _implicit : [])
        {
            Start(node, PrivilegePathStepKind.ImplicitGroup);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var parent in current.Parents)
            {
                if (predecessor.TryAdd(parent.Index, (current.Index, PrivilegePathStepKind.Group)))
                {
                    queue.Enqueue(parent);
                }
            }
        }

        return new MembershipPaths(this, account, predecessor);
    }

    /// <summary>
    /// The highest level a member of the group gets from <paramref name="levelOf"/> on the group itself or any group it is
    /// nested in (what membership of the group "grants"). Cached per graph; <paramref name="levelOf"/> must not change.
    /// </summary>
    public PrivilegeLevel GrantedLevel(GroupNode group, Func<GroupNode, PrivilegeLevel> levelOf)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(levelOf);
        lock (_gate)
        {
            if (_grantedLevelCache.TryGetValue(group.Index, out var cached))
            {
                return cached;
            }
        }

        var visited = new HashSet<int> { group.Index };
        var queue = new Queue<GroupNode>([group]);
        var max = PrivilegeLevel.None;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            max = PrivilegeLevels.Max(max, levelOf(current));
            foreach (var parent in current.Parents.Where(p => visited.Add(p.Index)))
            {
                queue.Enqueue(parent);
            }
        }

        lock (_gate)
        {
            _grantedLevelCache[group.Index] = max;
        }

        return max;
    }

    internal static string Key(string dn) =>
        (DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToString() : dn).ToUpperInvariant();

    internal GroupNode NodeAt(int index) => _nodes[index];
}

/// <summary>The groups an account reaches and how (result of <see cref="GroupGraph.Reach"/>).</summary>
public sealed class MembershipPaths
{
    private readonly GroupGraph _graph;
    private readonly PrivilegePathStep _account;
    private readonly Dictionary<int, (int From, PrivilegePathStepKind Kind)> _predecessor;

    internal MembershipPaths(GroupGraph graph, PrivilegePathStep account, Dictionary<int, (int From, PrivilegePathStepKind Kind)> predecessor)
    {
        _graph = graph;
        _account = account;
        _predecessor = predecessor;
    }

    /// <summary>Every reached group (the implicit principals included).</summary>
    public IEnumerable<GroupNode> Groups => _predecessor.Keys.Select(_graph.NodeAt);

    /// <summary>The groups the account is a direct member of (memberOf) or has as its primary group.</summary>
    public IEnumerable<GroupNode> DirectGroups =>
        _predecessor.Where(p => p.Value.From < 0 && p.Value.Kind != PrivilegePathStepKind.ImplicitGroup).Select(p => _graph.NodeAt(p.Key));

    public bool Contains(GroupNode group) => _predecessor.ContainsKey(group.Index);

    /// <summary>account → … → <paramref name="group"/> (shortest), or empty when the group is not reached.</summary>
    public IReadOnlyList<PrivilegePathStep> PathTo(GroupNode group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!_predecessor.ContainsKey(group.Index))
        {
            return [];
        }

        var steps = new List<PrivilegePathStep>();
        var index = group.Index;
        while (index >= 0)
        {
            var (from, kind) = _predecessor[index];
            var node = _graph.NodeAt(index);
            steps.Add(new PrivilegePathStep(node.Dn, node.Name, kind));
            index = from;
        }

        steps.Add(_account);
        steps.Reverse();
        return steps;
    }
}
