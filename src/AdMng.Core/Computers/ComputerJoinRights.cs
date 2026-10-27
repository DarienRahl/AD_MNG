using AdMng.Core.Security;

namespace AdMng.Core.Computers;

/// <summary>
/// "The following user or group can join this computer to a domain" (K1 design note 4.6): the ACEs ADUC writes on a
/// prestaged computer account for the chosen user or group – Reset Password (control access), read and write of the
/// Account Restrictions property set, validated write to DNS host name and to service principal name; all allow, no
/// inheritance.
/// </summary>
public static class ComputerJoinRights
{
    /// <summary>ADS_RIGHT_DS_CONTROL_ACCESS.</summary>
    public const int ControlAccess = 0x100;

    /// <summary>ADS_RIGHT_DS_READ_PROP | ADS_RIGHT_DS_WRITE_PROP.</summary>
    public const int ReadWriteProperty = 0x30;

    /// <summary>ADS_RIGHT_DS_SELF (validated write).</summary>
    public const int ValidatedWrite = 0x08;

    /// <summary>The four ACEs for <paramref name="trusteeSid"/>.</summary>
    /// <param name="trusteeSid">The user or group that may join the computer.</param>
    /// <param name="trusteeName">Its name for the preview (not written).</param>
    public static IReadOnlyList<AceSpec> For(string trusteeSid, string? trusteeName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trusteeSid);
        return
        [
            AceSpec.Allow(trusteeSid, ControlAccess, ExtendedRightIds.ResetPassword, trusteeName),
            AceSpec.Allow(trusteeSid, ReadWriteProperty, ExtendedRightIds.AccountRestrictions, trusteeName),
            AceSpec.Allow(trusteeSid, ValidatedWrite, ExtendedRightIds.ValidatedWriteDnsHostName, trusteeName),
            AceSpec.Allow(trusteeSid, ValidatedWrite, ExtendedRightIds.ValidatedWriteServicePrincipalName, trusteeName),
        ];
    }

    /// <summary>The delta that grants the join right.</summary>
    public static AclChange Grant(string trusteeSid, string? trusteeName = null) => new(For(trusteeSid, trusteeName), []);
}
