using AdMng.Core.ActiveDirectory;
using AdMng.Core.Schema;

namespace AdMng.Core.Comparison;

/// <summary>
/// Attribute classes of the comparison of two objects (design note docs/dev/multi-window.md 8.5 and 8.6): volatile and
/// identity attributes hidden by default, address attributes compared without their domain, DN-valued attributes
/// normalized to the portable form, and the attributes shown on the membership tabs instead of the attribute grid.
/// </summary>
public static class ComparisonAttributes
{
    private const string TpmPrefix = "msTPM-";

    private static readonly HashSet<string> VolatileSet = new(StringComparer.OrdinalIgnoreCase)
    {
        // Identity: always different between two objects (or two domains), never a configuration difference.
        Attr.ObjectGuid, Attr.ObjectSid, Attr.SidHistory, Attr.DistinguishedName, Attr.CanonicalName, Attr.MsDsPrincipalName,
        Attr.ObjectCategory, "instanceType", "mS-DS-ConsistencyGuid",

        // Times and counters.
        Attr.WhenCreated, Attr.WhenChanged, Attr.UsnCreated, Attr.UsnChanged, "modifyTimeStamp", "createTimeStamp", Attr.PwdLastSet,

        // Derived from pwdLastSet; whether the password expired (or never expires) shows in msDS-User-Account-Control-Computed
        // and userAccountControl, which stay visible.
        Attr.PasswordExpiryComputed,
        Attr.LastLogon, Attr.LastLogonTimestamp, "lastLogoff", Attr.LogonCount, Attr.BadPwdCount, Attr.BadPasswordTime, Attr.LockoutTime,

        // Replication.
        "dSCorePropagationData", "replPropertyMetaData", "msDS-ReplAttributeMetaData", "msDS-ReplValueMetaData",
        "replUpToDateVector", "repsFrom", "repsTo",

        // msDS-* logon and key counters.
        "msDS-LastSuccessfulInteractiveLogonTime", "msDS-LastFailedInteractiveLogonTime", "msDS-FailedInteractiveLogonCount",
        "msDS-FailedInteractiveLogonCountAtLastSuccessfulLogon", "msDS-KeyVersionNumber", "msDS-RevealedUsers", "msDS-RevealedList",
        "msDS-ManagedPasswordId", "msDS-ManagedPasswordPreviousId",

        // Computers.
        "msLAPS-PasswordExpirationTime", "ms-Mcs-AdmPwdExpirationTime",

        // DNS records (the Watchdog's never-diffed noise).
        "dnsRecord", "dNSTombstoned",
    };

    private static readonly HashSet<string> AddressSet = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.UserPrincipalName, Attr.Mail, Attr.ProxyAddresses, Attr.TargetAddress,
    };

    /// <summary>DN-valued attributes known by name, for a side whose schema does not describe them.</summary>
    private static readonly HashSet<string> KnownDnAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.Manager, Attr.ManagedBy, Attr.DirectReports, "secretary", "assistant", Attr.Member, Attr.MemberOf,
        Attr.DistinguishedName, Attr.ObjectCategory, "seeAlso", "msDS-AuthenticatedAtDC", "msExchCoManagedByLink",
        "publicDelegates", "publicDelegatesBL", "altRecipient", "msDS-HostServiceAccount", "msDS-HostServiceAccountBL",
        "serverReference", "frsComputerReference", "msDFSR-ComputerReference", "wellKnownObjects", "otherWellKnownObjects",
    };

    /// <summary>
    /// Volatile and identity attributes, shown only with "Pokaż atrybuty zmienne i identyfikujące" (without the
    /// <c>msTPM-*</c> prefix rule of <see cref="IsVolatile"/>).
    /// </summary>
    public static IReadOnlyCollection<string> Volatile => VolatileSet;

    /// <summary>Attributes compared without their domain part when "Porównuj adresy bez domeny" is on.</summary>
    public static IReadOnlyCollection<string> Addresses => AddressSet;

    /// <summary>True for a volatile or identity attribute (including every <c>msTPM-*</c> attribute). Options are ignored.</summary>
    public static bool IsVolatile(string attribute)
    {
        var name = BaseName(attribute);
        return VolatileSet.Contains(name) || name.StartsWith(TpmPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for userPrincipalName, mail, proxyAddresses and targetAddress.</summary>
    public static bool IsAddress(string attribute) => AddressSet.Contains(BaseName(attribute));

    /// <summary>
    /// True when the values are DNs (DN, DN-Binary or DN-String syntax) by either side's schema, or by name when neither
    /// schema describes the attribute.
    /// </summary>
    public static bool IsDnValued(string attribute, ISchemaCatalog? schemaA, ISchemaCatalog? schemaB)
    {
        var name = BaseName(attribute);
        var a = schemaA?.FindAttribute(name);
        var b = schemaB?.FindAttribute(name);
        return a is null && b is null ? KnownDnAttributes.Contains(name) : IsDnSyntax(a) || IsDnSyntax(b);
    }

    /// <summary>The attributes the membership tabs show instead of the attribute grid: memberOf, and member of a group.</summary>
    public static bool IsShownOnMembershipTabs(string attribute, bool isGroup)
    {
        var name = BaseName(attribute);
        return string.Equals(name, Attr.MemberOf, StringComparison.OrdinalIgnoreCase)
            || (isGroup && string.Equals(name, Attr.Member, StringComparison.OrdinalIgnoreCase));
    }

    internal static string BaseName(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        var separator = attribute.IndexOf(';', StringComparison.Ordinal);
        return separator < 0 ? attribute : attribute[..separator];
    }

    private static bool IsDnSyntax(AttributeDefinition? definition) =>
        definition?.Syntax is AttributeSyntax.DistinguishedName or AttributeSyntax.DnWithBinary or AttributeSyntax.DnWithString;
}
