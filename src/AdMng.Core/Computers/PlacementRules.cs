using AdMng.Core.Ldap;

namespace AdMng.Core.Computers;

/// <summary>Why a computer is in an unexpected container (K1 design note 4.1).</summary>
public enum PlacementFinding
{
    /// <summary>The computer is in the default computers container (wellKnownObjects, normally CN=Computers): not moved after the join.</summary>
    NotMovedAfterJoin,

    /// <summary>A domain controller outside OU=Domain Controllers (the Default Domain Controllers Policy misses it).</summary>
    DomainControllerOutsideOu,

    /// <summary>A computer that is no domain controller inside OU=Domain Controllers.</summary>
    MemberInDomainControllersOu,

    /// <summary>Outside every container the operator's rules allow for its type.</summary>
    UnexpectedContainer,
}

/// <summary>
/// Placement rules: built-in ones need no configuration (default computers container, the DC OU); the operator's rules
/// (settings: per type a list of allowed containers, portable DNs with <c>{domain}</c>, each with its subtree) add
/// <see cref="PlacementFinding.UnexpectedContainer"/>. A read-only DC follows the domain controller rules; an account of
/// unknown type gets the built-in findings only.
/// </summary>
public sealed class PlacementRules
{
    private readonly DistinguishedName? _defaultComputers;
    private readonly DistinguishedName? _domainControllers;
    private readonly Dictionary<ComputerType, List<DistinguishedName>> _allowed = [];

    /// <param name="defaultComputersDn">The default computers container of the domain (wellKnownObjects).</param>
    /// <param name="domainControllersDn">OU=Domain Controllers (wellKnownObjects).</param>
    /// <param name="allowed">The operator's rules, DNs already resolved; types without rules are not checked.</param>
    public PlacementRules(string? defaultComputersDn, string? domainControllersDn, IReadOnlyDictionary<ComputerType, IReadOnlyList<string>>? allowed = null)
    {
        _defaultComputers = Parse(defaultComputersDn);
        _domainControllers = Parse(domainControllersDn);
        foreach (var (type, containers) in allowed ?? new Dictionary<ComputerType, IReadOnlyList<string>>())
        {
            var parsed = containers.Select(Parse).OfType<DistinguishedName>().ToList();
            if (parsed.Count > 0)
            {
                _allowed[Normalize(type)] = parsed;
            }
        }
    }

    /// <summary>The operator configured rules for at least one type.</summary>
    public bool HasConfiguredRules => _allowed.Count > 0;

    /// <summary>The findings of a computer at <paramref name="distinguishedName"/>.</summary>
    public IReadOnlyList<PlacementFinding> Evaluate(string distinguishedName, ComputerType type)
    {
        if (!DistinguishedName.TryParse(distinguishedName, out var dn) || dn.Parent is not { } parent)
        {
            return [];
        }

        var findings = new List<PlacementFinding>();
        if (_defaultComputers is { } defaults && parent.Equals(defaults))
        {
            findings.Add(PlacementFinding.NotMovedAfterJoin);
        }

        if (_domainControllers is { } dcOu)
        {
            var inDcOu = dn.IsDescendantOf(dcOu);
            if (type.IsDomainController() && !inDcOu)
            {
                findings.Add(PlacementFinding.DomainControllerOutsideOu);
            }
            else if (!type.IsDomainController() && type != ComputerType.Unknown && inDcOu)
            {
                findings.Add(PlacementFinding.MemberInDomainControllersOu);
            }
        }

        if (_allowed.TryGetValue(Normalize(type), out var containers) && !containers.Any(c => dn.IsDescendantOf(c)))
        {
            findings.Add(PlacementFinding.UnexpectedContainer);
        }

        return findings;
    }

    /// <summary>A portable DN of the settings (<c>OU=Stacje,{domain}</c>) in a domain; null when it is not a valid DN.</summary>
    public static string? Resolve(string portableDn, string namingContext)
    {
        ArgumentNullException.ThrowIfNull(portableDn);
        ArgumentNullException.ThrowIfNull(namingContext);
        var resolved = portableDn.Replace(DomainPlaceholder, namingContext, StringComparison.OrdinalIgnoreCase);
        return DistinguishedName.TryParse(resolved, out var dn) && !dn.IsRoot ? dn.ToString() : null;
    }

    /// <summary>The placeholder of the domain naming context in portable DNs (as saved views use it).</summary>
    public const string DomainPlaceholder = "{domain}";

    private static ComputerType Normalize(ComputerType type) =>
        type == ComputerType.ReadOnlyDomainController ? ComputerType.DomainController : type;

    private static DistinguishedName? Parse(string? dn) =>
        dn is not null && DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot ? parsed : null;
}
