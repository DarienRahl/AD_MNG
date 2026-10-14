using AdMng.Core.ActiveDirectory;

namespace AdMng.Core.Schema;

/// <summary>
/// The one definition of attributes whose values are secrets: passwords and password hashes, LAPS, BitLocker and TPM
/// recovery information - plus every attribute the schema marks confidential (searchFlags 0x80,
/// <see cref="AttributeDefinition.IsConfidential"/>). Their values are never written to the audit log, are masked in
/// previews and reports, never appear as literals in generated scripts and are exported only after an explicit
/// confirmation.
/// </summary>
public static class SecretAttributes
{
    /// <summary>What is shown instead of a secret value.</summary>
    public const string Mask = "••••••••";

    private static readonly HashSet<string> WellKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.UnicodePwd, "userPassword", "unixUserPassword", "dBCSPwd", "lmPwdHistory", "ntPwdHistory", "supplementalCredentials",
        "ms-Mcs-AdmPwd", "msLAPS-Password", "msLAPS-EncryptedPassword", "msLAPS-EncryptedPasswordHistory",
        "msLAPS-EncryptedDSRMPassword", "msLAPS-EncryptedDSRMPasswordHistory",
        "msFVE-RecoveryPassword", "msFVE-KeyPackage", "msTPM-OwnerInformation", "msTPM-OwnerInformationTemp",
        "msDS-ManagedPassword", "msPKIAccountCredentials", "msPKIDPAPIMasterKeys",
        "initialAuthIncoming", "initialAuthOutgoing", "trustAuthIncoming", "trustAuthOutgoing",
    };

    /// <summary>Secret attributes known by name (whatever the schema says).</summary>
    public static IReadOnlyCollection<string> WellKnown => WellKnownNames;

    /// <summary>
    /// True when the values of the attribute are secret: a well-known secret attribute or one the schema marks confidential.
    /// Attribute options (<c>;binary</c>, <c>;range=...</c>) are ignored.
    /// </summary>
    /// <param name="attribute">LDAP display name.</param>
    /// <param name="schema">Schema of the session (null: only the well-known names).</param>
    public static bool IsSecret(string attribute, ISchemaCatalog? schema = null)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        var separator = attribute.IndexOf(';', StringComparison.Ordinal);
        var name = separator < 0 ? attribute : attribute[..separator];
        return WellKnownNames.Contains(name) || schema?.FindAttribute(name) is { IsConfidential: true };
    }
}
