namespace AdMng.Core.Pki;

/// <summary>Where a key credential comes from (KEYCREDENTIALLINK_ENTRY KeySource; <c>Cert.KeySource.&lt;KeyCredentialSource&gt;</c>).</summary>
public enum KeyCredentialSource
{
    /// <summary><c>KEY_SOURCE_AD</c> (0x00).</summary>
    ActiveDirectory,

    /// <summary><c>KEY_SOURCE_AZURE</c> (0x01).</summary>
    EntraId,

    /// <summary>Absent or another value.</summary>
    Unknown,
}

/// <summary>What a key credential is for (KEYCREDENTIALLINK_ENTRY KeyUsage; <c>Cert.KeyUsage.&lt;KeyCredentialUsage&gt;</c>).</summary>
public enum KeyCredentialUsage
{
    /// <summary>0x00 – admin key.</summary>
    AdminKey,

    /// <summary>0x01 – <c>KEY_USAGE_NGC</c> (Windows Hello for Business).</summary>
    Ngc,

    /// <summary>0x02 – transport key (STK).</summary>
    Stk,

    /// <summary>0x03 – BitLocker recovery.</summary>
    BitLockerRecovery,

    /// <summary>0x04 – other.</summary>
    Other,

    /// <summary>0x07 – <c>KEY_USAGE_FIDO</c>.</summary>
    Fido,

    /// <summary>0x08 – <c>KEY_USAGE_FEK</c> (file encryption key).</summary>
    Fek,

    /// <summary>0x09 – DPAPI.</summary>
    Dpapi,

    /// <summary>Absent or another value.</summary>
    Unknown,
}

/// <summary>
/// One value of <c>msDS-KeyCredentialLink</c> (docs/dev/p-certificate-services.md 2.4): the metadata of the
/// KEYCREDENTIALLINK_BLOB (version <c>0x0200</c>) – never the key material beyond the id (rule 2). <see cref="ToString"/>
/// carries the kinds only.
/// </summary>
/// <param name="KeyId">The KeyID entry (0x01), Base64 as <c>Get-ADUser</c> tools show it, or null.</param>
/// <param name="DeviceId">The DeviceId entry (0x06), or null.</param>
/// <param name="Source">KeySource (0x05).</param>
/// <param name="Usage">KeyUsage (0x04).</param>
/// <param name="Created">KeyCreationTime (0x09), or null.</param>
/// <param name="LastLogon">KeyApproximateLastLogonTimeStamp (0x08), or null.</param>
/// <param name="IsMalformed">The value could not be parsed (the row stays, the fields are null).</param>
public sealed record KeyCredentialLink(
    string? KeyId,
    Guid? DeviceId,
    KeyCredentialSource Source,
    KeyCredentialUsage Usage,
    DateTimeOffset? Created,
    DateTimeOffset? LastLogon,
    bool IsMalformed)
{
    /// <summary>
    /// Parses the values as <c>GetStrings</c> delivers them (<c>B:&lt;hexlen&gt;:&lt;hex&gt;:&lt;dn&gt;</c>, split with
    /// <c>WellKnownObjects.TryParseValue</c>); one entry per value, malformed ones marked.
    /// </summary>
    public static IReadOnlyList<KeyCredentialLink> Parse(IEnumerable<string> values) =>
        throw new NotImplementedException("P1 C4: KeyCredentialLink.Parse");

    /// <summary>Source, usage and whether it parsed only (never the key id or device id).</summary>
    public override string ToString() => $"{nameof(KeyCredentialLink)}({Source} {Usage}{(IsMalformed ? ", malformed" : string.Empty)})";
}
