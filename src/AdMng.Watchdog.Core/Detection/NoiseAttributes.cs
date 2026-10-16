using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Attributes that change constantly without meaning a change an operator cares about (section 9.3). They are never
/// diffed and never requested in the change searches, even when configured. Computer noise is diffed (the snapshot is
/// updated) but dropped unless the corresponding <see cref="DirectorySettings"/> switch is on.
/// </summary>
public static class NoiseAttributes
{
    private const string TpmPrefix = "msTPM-";

    private static readonly HashSet<string> NeverSet = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.LastLogon, Attr.LastLogonTimestamp, "lastLogoff", Attr.LogonCount, Attr.BadPwdCount, Attr.BadPasswordTime,
        Attr.WhenChanged, Attr.UsnChanged, Attr.UsnCreated, "dSCorePropagationData", WatchdogAttributes.ReplPropertyMetaData,
        WatchdogAttributes.ReplAttributeMetaData, "msDS-ReplValueMetaData", "modifyTimeStamp",
        "msDS-LastSuccessfulInteractiveLogonTime", "msDS-LastFailedInteractiveLogonTime", "msDS-FailedInteractiveLogonCount",
        "msDS-FailedInteractiveLogonCountAtLastSuccessfulLogon", "msDS-KeyVersionNumber", "msDS-RevealedUsers",
        "msDS-RevealedList", "msDS-ManagedPasswordId", "msDS-ManagedPasswordPreviousId", "msLAPS-PasswordExpirationTime",
        "ms-Mcs-AdmPwdExpirationTime", "dnsRecord", "dNSTombstoned", "replUpToDateVector", "repsFrom", "repsTo",
        Attr.MemberOf,
    };

    private static readonly HashSet<string> ComputerNoiseSet = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.PwdLastSet, Attr.OperatingSystem, Attr.OperatingSystemVersion, Attr.DnsHostName, Attr.ServicePrincipalName,
        Attr.SupportedEncryptionTypes, WatchdogAttributes.UserCertificate,
    };

    /// <summary>The never-diffed attributes (without the <c>msTPM-*</c> prefix rule).</summary>
    public static IReadOnlyCollection<string> Never => NeverSet;

    /// <summary>Attributes of computers whose changes are dropped unless enabled (section 9.3).</summary>
    public static IReadOnlyCollection<string> ComputerNoise => ComputerNoiseSet;

    /// <summary>True for an attribute that is never diffed (incl. every <c>msTPM-*</c> attribute). Options are ignored.</summary>
    public static bool IsNoise(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        var name = StripOptions(attribute);
        return NeverSet.Contains(name) || name.StartsWith(TpmPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a change of <paramref name="attribute"/> on a computer is dropped under <paramref name="settings"/>:
    /// pwdLastSet unless <see cref="DirectorySettings.TrackComputerPasswordChanges"/>, the OS/host attributes unless
    /// <see cref="DirectorySettings.TrackComputerOsChanges"/>. servicePrincipalName is decided per value
    /// (<see cref="IsOwnHostSpn"/>).
    /// </summary>
    public static bool IsDroppedComputerChange(string attribute, DirectorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(settings);
        var name = StripOptions(attribute);
        if (!ComputerNoiseSet.Contains(name))
        {
            return false;
        }

        return string.Equals(name, Attr.PwdLastSet, StringComparison.OrdinalIgnoreCase)
            ? !settings.TrackComputerPasswordChanges
            : !settings.TrackComputerOsChanges;
    }

    /// <summary>
    /// True when the host part of an SPN (<c>service/host[:port][/name]</c>) is the computer's own name: its
    /// dNSHostName or its NetBIOS name (sAMAccountName without <c>$</c>). Only such SPNs are computer noise.
    /// </summary>
    public static bool IsOwnHostSpn(string spn, string? dnsHostName, string? samAccountName)
    {
        ArgumentNullException.ThrowIfNull(spn);
        var slash = spn.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || slash == spn.Length - 1)
        {
            return false;
        }

        var host = spn[(slash + 1)..];
        var end = host.IndexOfAny([':', '/']);
        if (end >= 0)
        {
            host = host[..end];
        }

        if (dnsHostName is not null && string.Equals(host, dnsHostName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var netBios = samAccountName?.TrimEnd('$');
        return !string.IsNullOrEmpty(netBios) && string.Equals(host, netBios, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripOptions(string attribute)
    {
        var separator = attribute.IndexOf(';', StringComparison.Ordinal);
        return separator < 0 ? attribute : attribute[..separator];
    }
}
