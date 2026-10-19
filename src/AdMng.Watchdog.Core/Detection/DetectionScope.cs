using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Client-side scope of section 9.1: the default naming context minus <c>Directory.ExcludedSubtrees</c>,
/// <c>CN=Deleted Objects</c>, <c>CN=LostAndFound</c> and <c>CN=System</c> – except group policy containers under
/// <c>CN=Policies,CN=System</c>, trusts directly under CN=System, PSOs in the PSO container, <c>CN=RID Manager$</c>
/// and the critical objects. The configuration and schema NCs (lexically below the domain) are never in scope here.
/// </summary>
internal sealed class DetectionScope
{
    private readonly DetectionSettings _settings;
    private readonly DistinguishedName _domain;
    private readonly DistinguishedName _configuration;
    private readonly DistinguishedName _system;
    private readonly DistinguishedName _policies;
    private readonly DistinguishedName _passwordSettings;
    private readonly DistinguishedName _ridManager;
    private readonly DistinguishedName _infrastructure;
    private readonly DistinguishedName _deletedObjects;
    private readonly DistinguishedName _lostAndFound;

    public DetectionScope(DetectionSettings settings)
    {
        _settings = settings;
        _domain = DistinguishedName.Parse(settings.Facts.DomainDn);
        _configuration = DistinguishedName.Parse(settings.Facts.ConfigurationDn);
        _system = _domain.Child("CN", "System");
        _policies = _system.Child("CN", "Policies");
        _passwordSettings = _system.Child("CN", "Password Settings Container");
        _ridManager = _system.Child("CN", "RID Manager$");
        _infrastructure = _domain.Child("CN", "Infrastructure");
        _deletedObjects = _domain.Child("CN", "Deleted Objects");
        _lostAndFound = _domain.Child("CN", "LostAndFound");
    }

    /// <summary>True when an object of <paramref name="kind"/> at <paramref name="dn"/> is watched.</summary>
    public bool IsInScope(string dn, WatchObjectKind kind)
    {
        if (!DistinguishedName.TryParse(dn, out var parsed) || !Within(parsed, _domain) || Within(parsed, _configuration))
        {
            return false;
        }

        if (Within(parsed, _deletedObjects) || Within(parsed, _lostAndFound) || _settings.ExcludedSubtrees.Any(s => Within(parsed, s)))
        {
            return false;
        }

        if (kind == WatchObjectKind.Configuration)
        {
            return parsed == _ridManager || parsed == _infrastructure;
        }

        if (!Within(parsed, _system))
        {
            return true;
        }

        return kind switch
        {
            WatchObjectKind.GroupPolicy => parsed.IsDescendantOf(_policies),
            WatchObjectKind.Trust => parsed.Parent == _system,
            WatchObjectKind.PasswordSettings => parsed.IsDescendantOf(_passwordSettings),
            _ => _settings.CriticalObjects.Any(c => c == parsed),
        };
    }

    /// <summary>True for <paramref name="dn"/> below an excluded subtree (move out of scope → tag outOfScope).</summary>
    public bool IsExcluded(string dn) =>
        DistinguishedName.TryParse(dn, out var parsed) && _settings.ExcludedSubtrees.Any(s => Within(parsed, s));

    /// <summary>True when <paramref name="dn"/> is below the default NC (and not in the configuration NC).</summary>
    public bool IsInDomainPartition(string dn) =>
        DistinguishedName.TryParse(dn, out var parsed) && Within(parsed, _domain) && !Within(parsed, _configuration);

    /// <summary>
    /// True when <paramref name="dn"/> names an object of another naming context than this domain's – a member from
    /// another domain of the forest (also a child domain, lexically below this one): a search of this domain cannot tell
    /// whether it still exists. False for DNs that do not parse.
    /// </summary>
    public bool IsOtherDomain(string dn)
    {
        if (!DistinguishedName.TryParse(dn, out var parsed) || parsed.IsRoot)
        {
            return false;
        }

        return !Within(parsed, _domain)
            || parsed.Rdns.Take(parsed.Rdns.Count - _domain.Rdns.Count).Any(r => string.Equals(r.Type, "DC", StringComparison.OrdinalIgnoreCase));
    }

    private static bool Within(DistinguishedName dn, DistinguishedName ancestor) => dn == ancestor || dn.IsDescendantOf(ancestor);
}
