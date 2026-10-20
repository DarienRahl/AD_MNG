using System.Collections.Frozen;
using AdMng.Core.Ldap;
using AdMng.Core.Schema;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Parse-time reduction of ingested records (section 11.1, ADR-0008 cond. 2 – only what matching needs; also a flood
/// guard): 5136–5141 records under <c>ExcludedSubtrees</c> or of an unwatched object class are dropped; only the
/// <see cref="SecurityEventCatalog.RetainedFields"/> are kept; a 5136 <c>AttributeValue</c> and the SAM field values of
/// 4720/4738/4741/4742 are kept only for a watched attribute that is not secret – secret values become
/// <see cref="SecretAttributes.Mask"/>, other values are removed (the attribute name and operation stay). Without a
/// loaded schema every attribute counts as unwatched (no values at all).
/// </summary>
public static class SecurityEventReducer
{
    /// <summary>Object classes of the watched kinds (9.1), including the configuration, PKI and schema classes.</summary>
    public static IReadOnlySet<string> WatchedObjectClasses { get; } = new[]
    {
        "user", "inetOrgPerson", "computer", "group", "contact", "organizationalUnit", "container", "domainDNS",
        "groupPolicyContainer", "trustedDomain", "msDS-PasswordSettings", "msDS-ManagedServiceAccount",
        "msDS-GroupManagedServiceAccount", "msDS-DelegatedManagedServiceAccount", "rIDManager", "infrastructureUpdate",
        "nTDSDSA", "nTDSDSARO", "crossRefContainer", "nTDSService", "dMD", "pKICertificateTemplate", "pKIEnrollmentService",
        "certificationAuthority", "attributeSchema", "classSchema",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<int> SamEvents = new[]
    {
        SecurityEventCatalog.UserCreated, SecurityEventCatalog.UserChanged, SecurityEventCatalog.ComputerCreated, SecurityEventCatalog.ComputerChanged,
    }.ToFrozenSet();

    /// <summary>The reduced event, or null when the record is dropped.</summary>
    public static SecurityEvent? Reduce(SecurityEvent securityEvent, SecurityEventReduction reduction)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);
        ArgumentNullException.ThrowIfNull(reduction);
        if (SecurityEventCatalog.IsDirectoryServiceCode(securityEvent.EventCode) && !IsRelevantDirectoryChange(securityEvent, reduction))
        {
            return null;
        }

        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in securityEvent.Data)
        {
            if (SecurityEventCatalog.RetainedFields.Contains(name))
            {
                data[name] = value;
            }
        }

        if (securityEvent.EventCode == SecurityEventCatalog.ObjectModified && data.ContainsKey("AttributeValue"))
        {
            ReduceValue(data, "AttributeValue", securityEvent.Get("AttributeLDAPDisplayName"), reduction);
        }

        if (SamEvents.Contains(securityEvent.EventCode))
        {
            foreach (var (field, attribute) in SecurityEventCatalog.SamFieldAttributes)
            {
                if (data.ContainsKey(field) && field is not ("OldUacValue" or "NewUacValue"))
                {
                    ReduceValue(data, field, attribute, reduction);
                }
            }
        }

        return securityEvent with { Data = data };
    }

    /// <summary>
    /// Collector mode (stage B design 7.7): a logon or failure record (<see cref="SecurityEventCatalog.IsLogonRecord"/>)
    /// reduced to <see cref="SecurityEventCatalog.LogonRetainedFields"/>; null when it is dropped – a 4624 whose
    /// LogonType is not 3 or 10, of a computer account (<c>TargetUserName</c> ending in '$', with
    /// <see cref="AttributionSettings.DropComputerLogons"/>) or of ANONYMOUS LOGON (S-1-5-7, with
    /// <see cref="AttributionSettings.DropAnonymousLogons"/>), and any record of another event id.
    /// </summary>
    public static SecurityEvent? ReduceLogon(SecurityEvent securityEvent, AttributionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);
        ArgumentNullException.ThrowIfNull(settings);
        if (!SecurityEventCatalog.LogonRetainedFields.TryGetValue(securityEvent.EventCode, out var retained))
        {
            return null;
        }

        if (securityEvent.EventCode == SecurityEventCatalog.LogonSucceeded)
        {
            if (securityEvent.Get("LogonType")?.Trim() is not ("3" or "10"))
            {
                return null;
            }

            if (settings.DropComputerLogons && securityEvent.Get("TargetUserName")?.TrimEnd().EndsWith('$') == true)
            {
                return null;
            }

            if (settings.DropAnonymousLogons
                && (string.Equals(securityEvent.Get("TargetUserSid"), AnonymousLogonSid, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(securityEvent.Get("TargetUserName"), "ANONYMOUS LOGON", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
        }

        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in securityEvent.Data)
        {
            if (retained.Contains(name))
            {
                data[name] = value;
            }
        }

        return securityEvent with { Data = data };
    }

    /// <summary>ANONYMOUS LOGON.</summary>
    public const string AnonymousLogonSid = "S-1-5-7";

    private static bool IsRelevantDirectoryChange(SecurityEvent securityEvent, SecurityEventReduction reduction)
    {
        if (securityEvent.Get("ObjectClass") is { } objectClass && !WatchedObjectClasses.Contains(objectClass))
        {
            return false;
        }

        // 5139: relevant unless both ends are excluded (a move into or out of an excluded subtree changes the scope).
        return securityEvent.EventCode == SecurityEventCatalog.ObjectMoved
            ? !IsExcluded(securityEvent.Get("OldObjectDN"), reduction) || !IsExcluded(securityEvent.Get("NewObjectDN"), reduction)
            : !IsExcluded(securityEvent.Get("ObjectDN"), reduction);
    }

    private static bool IsExcluded(string? value, SecurityEventReduction reduction) =>
        DistinguishedName.TryParse(value, out var dn) && !dn.IsRoot
        && reduction.ExcludedSubtrees.Any(excluded => dn.Equals(excluded) || dn.IsDescendantOf(excluded));

    private static void ReduceValue(Dictionary<string, string> data, string field, string? attribute, SecurityEventReduction reduction)
    {
        if (string.IsNullOrEmpty(attribute))
        {
            data.Remove(field);
        }
        else if (SecretAttributes.IsSecret(attribute, reduction.Schema))
        {
            data[field] = SecretAttributes.Mask;
        }
        else if (reduction.Schema is null || !reduction.WatchedAttributes.IsWatched(attribute))
        {
            data.Remove(field);
        }
    }
}
