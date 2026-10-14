namespace AdMng.Watchdog.Core.Detection;

/// <summary>One domain controller of the forest (an nTDSDSA object under CN=Sites).</summary>
/// <param name="ObjectGuid">objectGUID of the NTDS Settings object.</param>
/// <param name="NtdsSettingsDn">DN of the NTDS Settings object.</param>
/// <param name="HostName">dNSHostName of the parent server object (null when unreadable).</param>
/// <param name="InvocationId">Current invocationId.</param>
/// <param name="IsReadOnly">RODC (objectCategory nTDSDSARO) – never an originating DC.</param>
/// <param name="IsThisDomain">msDS-HasDomainNCs contains the watched domain NC.</param>
public sealed record KnownDomainController(
    Guid ObjectGuid,
    string NtdsSettingsDn,
    string? HostName,
    Guid? InvocationId,
    bool IsReadOnly,
    bool IsThisDomain)
{
    /// <summary>Upper-case NetBIOS name (first DNS label of the host, else the server object's RDN).</summary>
    public string NetBiosName =>
        (HostName?.Split('.')[0] ?? AdMng.Core.Ldap.DistinguishedName.Parse(NtdsSettingsDn).Parent?.Name ?? string.Empty).ToUpperInvariant();
}
