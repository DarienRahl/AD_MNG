using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;

namespace AdMng.Watchdog.Core.Gpo;

/// <summary>Where a GPO is linked (stage B design 8.6), from the gPLink snapshot of the domain head, the OUs and the sites.</summary>
public sealed record GpoLinkScope
{
    /// <summary>At least one gPLink names the GPO – enforced or not, also a disabled link (it can be enabled without a
    /// content change; the link change itself is a GroupPolicyLinkChanged event).</summary>
    public bool Linked { get; init; }

    public bool LinkedToDomainHead { get; init; }

    public bool LinkedToDomainControllers { get; init; }

    /// <summary>Linked to the domain head or the Domain Controllers OU – the Critical scope of 8.6.</summary>
    public bool Tier0 => LinkedToDomainHead || LinkedToDomainControllers;

    public static GpoLinkScope Unlinked { get; } = new();

    /// <summary>
    /// Evaluates the gPLink values of <paramref name="containers"/> (container DN, gPLink) for the GPO. Links are matched by
    /// the GUID of the GPO DN's <c>cn</c>; the Domain Controllers OU is <c>OU=Domain Controllers,&lt;domain&gt;</c>.
    /// </summary>
    public static GpoLinkScope From(Guid gpoGuid, string domainDn, IEnumerable<(string ContainerDn, string? GpLink)> containers)
    {
        ArgumentNullException.ThrowIfNull(containers);
        if (!DistinguishedName.TryParse(domainDn, out var domain) || domain.IsRoot)
        {
            throw new ArgumentException("The domain DN is invalid.", nameof(domainDn));
        }

        var domainControllers = domain.Child("OU", "Domain Controllers");
        bool linked = false, head = false, dcs = false;
        foreach (var (containerDn, gpLink) in containers)
        {
            if (!GpLinks.Parse(gpLink).Any(l => Links(l.Dn, gpoGuid)))
            {
                continue;
            }

            linked = true;
            if (DistinguishedName.TryParse(containerDn, out var container))
            {
                head |= container == domain;
                dcs |= container == domainControllers;
            }
        }

        return new GpoLinkScope { Linked = linked, LinkedToDomainHead = head, LinkedToDomainControllers = dcs };
    }

    private static bool Links(string gpoDn, Guid gpoGuid) =>
        DistinguishedName.TryParse(gpoDn, out var dn) && !dn.IsRoot
        && GpoPathValidator.TryParseGpcCn(dn.Name, out var guid) && guid == gpoGuid;
}

/// <summary>Severities of the GPO content kinds (stage B design 8.6).</summary>
public static class GpoSeverityClassifier
{
    /// <summary>
    /// GroupPolicySettingsChanged: Critical when a sensitive category changed in a GPO linked to the domain head or the
    /// Domain Controllers OU, or when the GPO carries a GPP <c>cpassword</c>; High for a sensitive category elsewhere;
    /// Medium for a linked GPO; Low for an unlinked one.
    /// </summary>
    public static Severity SettingsChanged(GpoSettingsDiff diff, GpoLinkScope scope)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(scope);
        if ((scope.Tier0 && diff.SensitiveChanged) || diff.GppPasswordPresent)
        {
            return Severity.Critical;
        }

        return diff.SensitiveChanged ? Severity.High : scope.Linked ? Severity.Medium : Severity.Low;
    }

    /// <summary>GroupPolicyContentOutOfBand: High, Critical when linked to the domain head or the Domain Controllers OU.</summary>
    public static Severity ContentOutOfBand(GpoLinkScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Tier0 ? Severity.Critical : Severity.High;
    }
}
