using AdMng.Core.Ldap;

namespace AdMng.Core.GroupPolicy;

/// <summary>A GPO that applies to an object, with the link it applies through.</summary>
/// <param name="GpoId">The GPO's GUID.</param>
/// <param name="Link">The link (order, enforced).</param>
/// <param name="Container">The site, domain head or OU of the link.</param>
public sealed record AppliedGroupPolicy(Guid GpoId, GroupPolicyLink Link, GroupPolicySom Container);

/// <summary>
/// Which GPOs apply to an object and in which order ([MS-GPOL] 3.2.5.1, as GPMC's Group Policy Inheritance shows it):
/// site, domain, then the OUs from the top down to the object's parent; within a container the links from the highest link
/// order number to link order 1 (the last link of <c>gPLink</c> is applied last and wins). Disabled links are skipped. An
/// OU (or the domain head) with block inheritance drops the links of every container above it unless they are enforced.
/// Enforced links are applied after all others, the deepest container first and the site last, so that the enforced link
/// highest in the hierarchy wins. A GPO linked more than once counts once, at its last (winning) position.
/// </summary>
public sealed class GroupPolicyInheritance
{
    private readonly DistinguishedName _domain;
    private readonly Dictionary<DistinguishedName, GroupPolicySom> _containers = [];
    private readonly Dictionary<DistinguishedName, GroupPolicySom> _sites = [];

    /// <param name="domainDn">The domain head (the top of the OU chain).</param>
    /// <param name="containers">The domain head, the OUs and the sites with their links.</param>
    public GroupPolicyInheritance(string domainDn, IEnumerable<GroupPolicySom> containers)
    {
        ArgumentNullException.ThrowIfNull(containers);
        _domain = DistinguishedName.Parse(domainDn);
        foreach (var container in containers)
        {
            if (!DistinguishedName.TryParse(container.Dn, out var dn))
            {
                continue;
            }

            (container.Kind == GroupPolicySomKind.Site ? _sites : _containers)[dn] = container;
        }
    }

    /// <summary>The GPOs that apply to an object below <paramref name="parentDn"/>, in application order (the last wins).</summary>
    /// <param name="parentDn">The container of the object (its OU, CN=Computers, the domain head …).</param>
    /// <param name="siteDn">The object's site, null when unknown (then no site links apply).</param>
    public IReadOnlyList<AppliedGroupPolicy> Resolve(string parentDn, string? siteDn)
    {
        // The chain from the top: the site, the domain head, every OU from the domain down to the parent.
        var chain = new List<GroupPolicySom>();
        if (siteDn is not null && DistinguishedName.TryParse(siteDn, out var site) && _sites.TryGetValue(site, out var siteSom))
        {
            chain.Add(siteSom);
        }

        if (_containers.TryGetValue(_domain, out var head))
        {
            chain.Add(head);
        }

        if (DistinguishedName.TryParse(parentDn, out var parent) && (parent == _domain || parent.IsDescendantOf(_domain)))
        {
            var below = new List<GroupPolicySom>();
            for (var dn = parent; dn is not null && dn != _domain; dn = dn.Parent)
            {
                if (_containers.TryGetValue(dn, out var som) && som.Kind == GroupPolicySomKind.OrganizationalUnit)
                {
                    below.Add(som);
                }
            }

            below.Reverse();
            chain.AddRange(below);
        }

        // A container is blocked when a container below it (in the chain) blocks inheritance.
        var blocked = new bool[chain.Count];
        var blocking = false;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            blocked[i] = blocking;
            blocking |= chain[i].BlocksInheritance && chain[i].Kind != GroupPolicySomKind.Site;
        }

        var applied = new List<AppliedGroupPolicy>();
        for (var i = 0; i < chain.Count; i++)
        {
            if (!blocked[i])
            {
                Add(applied, chain[i], enforced: false);
            }
        }

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            Add(applied, chain[i], enforced: true);
        }

        // A GPO counts once, at its last (winning) position.
        var seen = new HashSet<Guid>();
        var result = new List<AppliedGroupPolicy>();
        for (var i = applied.Count - 1; i >= 0; i--)
        {
            if (seen.Add(applied[i].GpoId))
            {
                result.Add(applied[i]);
            }
        }

        result.Reverse();
        return result;
    }

    private static void Add(List<AppliedGroupPolicy> applied, GroupPolicySom container, bool enforced)
    {
        // gPLink order: the first link has the highest link order number (lowest precedence) and is applied first.
        foreach (var link in container.Links)
        {
            if (!link.Disabled && link.Enforced == enforced && link.GpoId is { } id)
            {
                applied.Add(new AppliedGroupPolicy(id, link, container));
            }
        }
    }
}
