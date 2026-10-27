namespace AdMng.Core.Security;

/// <summary>
/// rightsGuid values of the extended rights, validated writes and property sets the pipeline writes into DACLs (the
/// join right of a prestaged computer, K1; security filtering and delegation of Group Policy objects, K2). They are the
/// same in every forest (schema and configuration constants).
/// </summary>
public static class ExtendedRightIds
{
    /// <summary>Reset Password (User-Force-Change-Password), control access.</summary>
    public static readonly Guid ResetPassword = new("00299570-246d-11d0-a768-00aa006e0529");

    /// <summary>Validated write to DNS host name (Validated-DNS-Host-Name), self (validated write).</summary>
    public static readonly Guid ValidatedWriteDnsHostName = new("72e39547-7b18-11d1-adef-00c04fd8d5cd");

    /// <summary>Validated write to service principal name (Validated-SPN), self (validated write).</summary>
    public static readonly Guid ValidatedWriteServicePrincipalName = new("f3a64788-5306-11d1-a9c5-0000f80367c1");

    /// <summary>Account Restrictions property set (User-Account-Restrictions), read/write property.</summary>
    public static readonly Guid AccountRestrictions = new("4c164200-20c0-11d0-a768-00aa006e0529");

    /// <summary>Apply Group Policy (Apply-Group-Policy), control access on a groupPolicyContainer.</summary>
    public static readonly Guid ApplyGroupPolicy = new("edacfd8f-ffb3-11d1-b41d-00a0c968f939");
}
