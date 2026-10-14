using System.Collections.Frozen;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>
/// The noise attributes of section 9.3 as the validator needs them (<c>invalidAttribute</c>: a noise attribute can be
/// neither watched nor used as a rule condition). The detection side keeps its own list in
/// <c>Detection/NoiseAttributes</c> (W-detect); both follow 9.3 and are unified at integration.
/// </summary>
internal static class NoiseAttributeNames
{
    private static readonly FrozenSet<string> Names = new[]
    {
        "lastLogon", "lastLogonTimestamp", "lastLogoff", "logonCount", "badPwdCount", "badPasswordTime", "whenChanged",
        "uSNChanged", "uSNCreated", "dSCorePropagationData", "replPropertyMetaData", "msDS-ReplAttributeMetaData",
        "msDS-ReplValueMetaData", "modifyTimeStamp", "msDS-LastSuccessfulInteractiveLogonTime",
        "msDS-LastFailedInteractiveLogonTime", "msDS-FailedInteractiveLogonCount",
        "msDS-FailedInteractiveLogonCountAtLastSuccessfulLogon", "msDS-KeyVersionNumber", "msDS-RevealedUsers",
        "msDS-RevealedList", "msDS-ManagedPasswordId", "msDS-ManagedPasswordPreviousId", "msLAPS-PasswordExpirationTime",
        "ms-Mcs-AdmPwdExpirationTime", "dnsRecord", "dNSTombstoned", "replUpToDateVector", "repsFrom", "repsTo", "memberOf",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private const string TpmPrefix = "msTPM-";

    /// <summary>True for a noise attribute of 9.3 (including the <c>msTPM-*</c> prefix).</summary>
    public static bool IsNoise(string attribute) =>
        Names.Contains(attribute) || attribute.StartsWith(TpmPrefix, StringComparison.OrdinalIgnoreCase);
}
