using AdMng.Core.Ldap;

namespace AdMng.Core.GroupPolicy;

/// <summary>A GPO that applies to an object, with the link it applies through.</summary>
/// <param name="GpoId">The GPO's GUID.</param>
/// <param name="Link">The link (order, enforced).</param>
/// <param name="Container">The site, domain head or OU of the link.</param>
public sealed record AppliedGroupPolicy(Guid GpoId, GroupPolicyLink Link, GroupPolicySom Container);

/// <summary>Why a link of the chain does not apply its GPO (<see cref="GroupPolicyInheritance.Explain"/>).</summary>
public enum GroupPolicyLinkExclusion
{
    /// <summary>The link is disabled (<c>gPLink</c> option 1).</summary>
    LinkDisabled,

    /// <summary>A container below the link's container blocks inheritance and the link is not enforced.</summary>
    Blocked,

    /// <summary>The GPO is linked again with a higher precedence: it counts there.</summary>
    Duplicate,
}

/// <summary>A link of the chain that does not apply its GPO.</summary>
/// <param name="GpoId">The GPO's GUID.</param>
/// <param name="Link">The link.</param>
/// <param name="Container">The site, domain head or OU of the link.</param>
/// <param name="Reason">Why it does not apply.</param>
/// <param name="BlockedBy">For <see cref="GroupPolicyLinkExclusion.Blocked"/>: the nearest container below that blocks inheritance.</param>
public sealed record ExcludedGroupPolicyLink(Guid GpoId, GroupPolicyLink Link, GroupPolicySom Container, GroupPolicyLinkExclusion Reason, string? BlockedBy = null);

/// <summary>The links of an object's chain: the GPOs in application order (the last wins) and the links left out, with why.</summary>
/// <param name="Applied">The GPOs that apply, in application order.</param>
/// <param name="Excluded">The links that do not apply (chain order).</param>
public sealed record GroupPolicyChain(IReadOnlyList<AppliedGroupPolicy> Applied, IReadOnlyList<ExcludedGroupPolicyLink> Excluded);

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
    public IReadOnlyList<AppliedGroupPolicy> Resolve(string parentDn, string? siteDn) => Explain(parentDn, siteDn).Applied;

    /// <summary>
    /// <see cref="Resolve"/> with the links it leaves out (K5): disabled links, links blocked by a container below that
    /// blocks inheritance (with that container) and links of a GPO linked again with a higher precedence. Links whose
    /// <c>cn</c> is not a GPO GUID are not reported.
    /// </summary>
    /// <param name="parentDn">The container of the object (its OU, CN=Computers, the domain head …).</param>
    /// <param name="siteDn">The object's site, null when unknown (then no site links apply).</param>
    public GroupPolicyChain Explain(string parentDn, string? siteDn)
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

        // A container is blocked when a container below it (in the chain) blocks inheritance: the nearest such container.
        var blockedBy = new string?[chain.Count];
        string? blocking = null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            blockedBy[i] = blocking;
            if (chain[i].BlocksInheritance && chain[i].Kind != GroupPolicySomKind.Site)
            {
                blocking = chain[i].Dn;
            }
        }

        var excluded = new List<ExcludedGroupPolicyLink>();
        var applied = new List<AppliedGroupPolicy>();
        for (var i = 0; i < chain.Count; i++)
        {
            // gPLink order: the first link has the highest link order number (lowest precedence) and is applied first.
            foreach (var link in chain[i].Links)
            {
                if (link.GpoId is not { } id)
                {
                    continue;
                }

                if (link.Disabled)
                {
                    excluded.Add(new ExcludedGroupPolicyLink(id, link, chain[i], GroupPolicyLinkExclusion.LinkDisabled));
                }
                else if (!link.Enforced && blockedBy[i] is { } by)
                {
                    excluded.Add(new ExcludedGroupPolicyLink(id, link, chain[i], GroupPolicyLinkExclusion.Blocked, by));
                }
                else if (!link.Enforced)
                {
                    applied.Add(new AppliedGroupPolicy(id, link, chain[i]));
                }
            }
        }

        // Enforced links last, the deepest container first, so that the enforced link highest in the hierarchy wins.
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            foreach (var link in chain[i].Links)
            {
                if (!link.Disabled && link.Enforced && link.GpoId is { } id)
                {
                    applied.Add(new AppliedGroupPolicy(id, link, chain[i]));
                }
            }
        }

        // A GPO counts once, at its last (winning) position. A GPO is its container's DN: the default GPOs have the same
        // GUID in every domain, and a site or another domain's OU may link the GPO of another domain.
        var seen = new HashSet<DistinguishedName>();
        var result = new List<AppliedGroupPolicy>();
        for (var i = applied.Count - 1; i >= 0; i--)
        {
            if (seen.Add(DistinguishedName.Parse(applied[i].Link.GpoDn)))
            {
                result.Add(applied[i]);
            }
            else
            {
                excluded.Add(new ExcludedGroupPolicyLink(applied[i].GpoId, applied[i].Link, applied[i].Container, GroupPolicyLinkExclusion.Duplicate));
            }
        }

        result.Reverse();
        return new GroupPolicyChain(result, excluded);
    }
}
