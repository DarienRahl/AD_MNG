using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.Pki;

/// <summary>What a <see cref="PkiAclGrant"/> is on (docs/dev/p-certificate-services.md 2.3).</summary>
public enum PkiAclTargetKind
{
    /// <summary>A container of the PKI subtree (<c>CN=Public Key Services</c> and below).</summary>
    Container,

    /// <summary><c>CN=NTAuthCertificates</c>.</summary>
    NtAuth,

    /// <summary>An <c>msPKI-Enterprise-Oid</c> object.</summary>
    Oid,

    /// <summary>An enterprise CA (<c>pKIEnrollmentService</c>).</summary>
    EnrollmentService,

    /// <summary>A CA / AIA store object (<c>certificationAuthority</c>).</summary>
    CertificationAuthority,

    /// <summary>A <c>cRLDistributionPoint</c>.</summary>
    CrlDistributionPoint,

    /// <summary>A certificate template (ESC4 – excluded from ESC5).</summary>
    Template,

    /// <summary>An enterprise CA's computer object.</summary>
    CaComputer,

    /// <summary>A Tier 0 account (ESC14 – <c>altSecurityIdentities</c> writes).</summary>
    Tier0Account,
}

/// <summary>
/// A right beyond the defaults on a PKI object, a CA computer or a Tier 0 account, as
/// <c>DirectoryAclGrantSource.CollectPublicKeyAsync</c> returns it (typed – <c>PrivilegeGrant.Values</c> are formatted
/// strings). In memory only.
/// </summary>
/// <param name="TargetDn">The object.</param>
/// <param name="TargetKind">What it is.</param>
/// <param name="TrusteeSid">Who holds the right.</param>
/// <param name="Right">The right (U4b's names).</param>
/// <param name="Detail">The attribute or class of the right (as <c>AclRights.Format</c>), or null.</param>
public sealed record PkiAclGrant(string TargetDn, PkiAclTargetKind TargetKind, string TrusteeSid, AclRight Right, string? Detail)
{
    /// <summary>Kind and right only (never the DN, SID or detail).</summary>
    public override string ToString() => $"{nameof(PkiAclGrant)}({TargetKind} {Right})";
}
