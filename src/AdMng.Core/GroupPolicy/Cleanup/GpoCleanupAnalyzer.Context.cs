using System.Globalization;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Core.Ldap;

namespace AdMng.Core.GroupPolicy.Cleanup;

public static partial class GpoCleanupAnalyzer
{
    /// <summary>
    /// Lookups of one analysis: entries by GUID, link and filter keys, and where each GPO applies (resolved once per
    /// container and site with <see cref="GroupPolicyInheritance"/>, on demand). Uses only the index's data, never the
    /// W1 lookups of <see cref="GpoSettingsIndex"/>, so that the analyzer stays a pure function of the records.
    /// </summary>
    private sealed class AnalysisContext
    {
        private readonly Dictionary<Guid, GpoIndexEntry> _byId = [];
        private readonly Dictionary<Guid, string> _scopeKeys = [];
        private readonly Dictionary<(string Container, string? Site), HashSet<Guid>> _applied = [];
        private readonly Dictionary<Guid, IReadOnlyList<(string Container, string? Site)>?> _positions = [];
        private readonly Dictionary<(Guid Keeper, Guid Covered), bool> _covers = [];
        private readonly Dictionary<Guid, GroupPolicySecurityFilter?> _filters = [];
        private readonly Dictionary<Guid, bool> _overlapsUnknown = [];
        private readonly List<GroupPolicyContainerInfo> _domainContainers;
        private readonly HashSet<DistinguishedName> _knownContainers = [];
        private readonly DistinguishedName? _domain;
        private GroupPolicyInheritance? _inheritance;
        private HashSet<Guid>? _unknown;

        public AnalysisContext(GpoSettingsIndex index)
        {
            Index = index;
            var entries = new List<GpoIndexEntry>();
            foreach (var entry in index.Entries)
            {
                if (entry?.Gpo is not null && _byId.TryAdd(entry.Gpo.Id, entry))
                {
                    entries.Add(entry);
                }
            }

            Entries = entries;
            _domain = DistinguishedName.TryParse(index.DomainDn, out var domain) ? domain : null;
            _domainContainers = [.. index.Containers.Where(c => c.Kind != GroupPolicySomKind.Site)];
            foreach (var container in index.Containers)
            {
                if (DistinguishedName.TryParse(container.Dn, out var dn))
                {
                    _knownContainers.Add(dn);
                }
            }
        }

        public GpoSettingsIndex Index { get; }

        /// <summary>The entries, one per GUID, in index order.</summary>
        public IReadOnlyList<GpoIndexEntry> Entries { get; }

        public GpoIndexEntry? Find(Guid id) => _byId.GetValueOrDefault(id);

        public string NameOf(Guid id) => _byId.TryGetValue(id, out var entry) ? entry.Gpo.DisplayName : id.ToString("B", CultureInfo.InvariantCulture);

        /// <summary>The GPO has an enabled link on the domain head.</summary>
        public bool LinkedToDomainHead(GpoIndexEntry entry) =>
            entry.Links.Any(l => l.Enabled && l.Kind == GroupPolicySomKind.Domain
                && (_domain is null || (DistinguishedName.TryParse(l.ContainerDn, out var dn) && dn == _domain)));

        /// <summary>
        /// The link set (container, enabled, enforced), the security filter (allowed, denied) and the WMI filter as one key:
        /// GPOs with equal keys reach the same objects in the same way (merge candidates).
        /// </summary>
        public string ScopeKey(GpoIndexEntry entry)
        {
            if (_scopeKeys.TryGetValue(entry.Gpo.Id, out var key))
            {
                return key;
            }

            var links = entry.Links
                .Select(l => NormalizeDn(l.ContainerDn) + "|" + (l.Enabled ? "1" : "0") + (l.Enforced ? "1" : "0"))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);

            // An unknown security filter (descriptor not readable) is never "the same" as another one: the key is unique.
            var filter = FilterOf(entry) is { } known ? FilterKey(known) : "?" + entry.Gpo.Id.ToString("N", CultureInfo.InvariantCulture);
            key = string.Join(";", links) + "#" + filter + "#" + (entry.Gpo.WmiFilterId ?? string.Empty).ToUpperInvariant();
            _scopeKeys[entry.Gpo.Id] = key;
            return key;
        }

        /// <summary>
        /// <paramref name="keeper"/> applies wherever <paramref name="covered"/> applies: in every container (domain head, OU)
        /// and site combination where <paramref name="covered"/> is in the resolved chain, <paramref name="keeper"/> is too;
        /// its security filter applies to everyone the covered GPO's does and its WMI filter is none or the same.
        /// </summary>
        public bool Covers(GpoIndexEntry keeper, GpoIndexEntry covered)
        {
            if (_covers.TryGetValue((keeper.Gpo.Id, covered.Gpo.Id), out var cached))
            {
                return cached;
            }

            var result = SecurityFilterCovers(FilterOf(keeper), FilterOf(covered))
                && (keeper.Gpo.WmiFilterId is not { } wmi || string.Equals(wmi, covered.Gpo.WmiFilterId, StringComparison.OrdinalIgnoreCase))
                && Positions(covered) is { Count: > 0 } positions
                && positions.All(p => Applied(p.Container, p.Site).Contains(keeper.Gpo.Id));
            _covers[(keeper.Gpo.Id, covered.Gpo.Id)] = result;
            return result;
        }

        /// <summary>
        /// A GPO whose content is not known (not read, read incompletely, failed) applies in a container where
        /// <paramref name="entry"/> applies: it may hold another value of any setting there, between the two copies of a
        /// duplicate in precedence, so no duplicate of <paramref name="entry"/> can be shown to be redundant.
        /// </summary>
        public bool OverlapsUnknownContent(GpoIndexEntry entry)
        {
            if (_overlapsUnknown.TryGetValue(entry.Gpo.Id, out var cached))
            {
                return cached;
            }

            _unknown ??= [.. Entries.Where(e => e.Status is not (GpoIndexStatus.Read or GpoIndexStatus.NotFound)
                && e.Gpo.Status != GpoStatus.AllSettingsDisabled && e.Links.Any(l => l.Enabled)).Select(e => e.Gpo.Id)];
            var result = Positions(entry) is not { } positions
                || (_unknown.Count > 0 && positions.Any(p => Applied(p.Container, p.Site).Overlaps(_unknown)));
            _overlapsUnknown[entry.Gpo.Id] = result;
            return result;
        }

        /// <summary>
        /// Where the GPO applies: (container, site) pairs, the site null for objects whose site links nothing of this GPO.
        /// Null when one of its enabled links is on a container the index does not know (coverage cannot be shown).
        /// </summary>
        private IReadOnlyList<(string Container, string? Site)>? Positions(GpoIndexEntry entry)
        {
            if (_positions.TryGetValue(entry.Gpo.Id, out var cached))
            {
                return cached;
            }

            List<(string, string?)>? result = [];
            var enabled = entry.Links.Where(l => l.Enabled).ToList();
            if (enabled.Any(l => !DistinguishedName.TryParse(l.ContainerDn, out var dn) || !_knownContainers.Contains(dn)))
            {
                result = null;
            }
            else if (Index.OtherDomains.Count > 0 && enabled.Any(l => l.Kind == GroupPolicySomKind.Site))
            {
                // A site link also reaches the objects of the forest's other domains in that site, whose containers (and
                // the links there) this DC does not show.
                result = null;
            }
            else
            {
                string?[] sites = [null, .. enabled.Where(l => l.Kind == GroupPolicySomKind.Site).Select(l => NormalizeDn(l.ContainerDn)).Distinct(StringComparer.Ordinal)];
                foreach (var container in _domainContainers)
                {
                    foreach (var site in sites)
                    {
                        if (Applied(container.Dn, site).Contains(entry.Gpo.Id))
                        {
                            result.Add((container.Dn, site));
                        }
                    }
                }
            }

            _positions[entry.Gpo.Id] = result;
            return result;
        }

        private HashSet<Guid> Applied(string container, string? site)
        {
            if (_applied.TryGetValue((container, site), out var applied))
            {
                return applied;
            }

            _inheritance ??= new GroupPolicyInheritance(Index.DomainDn, Index.Containers.Select(c => c.ToSom()));
            applied = [.. _inheritance.Resolve(container, site).Select(a => a.GpoId)];
            _applied[(container, site)] = applied;
            return applied;
        }

        private static string NormalizeDn(string dn) =>
            (DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToString() : dn.Trim()).ToUpperInvariant();

        /// <summary>The GPO's security filter, parsed once per analysis (null: no readable descriptor).</summary>
        private GroupPolicySecurityFilter? FilterOf(GpoIndexEntry entry)
        {
            if (!_filters.TryGetValue(entry.Gpo.Id, out var filter))
            {
                filter = entry.Gpo.SecurityFilter;
                _filters[entry.Gpo.Id] = filter;
            }

            return filter;
        }

        private static string FilterKey(GroupPolicySecurityFilter filter) =>
            string.Join(",", filter.Allowed.Select(s => s.ToUpperInvariant()).Order(StringComparer.Ordinal)) + "|"
            + string.Join(",", filter.Denied.Select(s => s.ToUpperInvariant()).Order(StringComparer.Ordinal));

        /// <summary>
        /// The keeper's filter applies to everyone the covered GPO's does: the same sets, or Authenticated Users / Everyone
        /// allowed with no deny. An unknown filter (no descriptor read) neither covers nor is covered: who it reaches
        /// cannot be shown.
        /// </summary>
        private static bool SecurityFilterCovers(GroupPolicySecurityFilter? keeper, GroupPolicySecurityFilter? covered)
        {
            if (keeper is null || covered is null)
            {
                return false;
            }

            if (keeper.Denied.Count == 0 && keeper.Allowed.Any(s =>
                string.Equals(s, GroupPolicySecurityFilter.AuthenticatedUsersSid, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, GroupPolicySecurityFilter.EveryoneSid, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return FilterKey(keeper) == FilterKey(covered);
        }
    }
}
