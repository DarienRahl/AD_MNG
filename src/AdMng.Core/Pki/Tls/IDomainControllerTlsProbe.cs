using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;

namespace AdMng.Core.Pki.Tls;

/// <summary>
/// The TLS handshake probe of a DC's LDAPS / GC-LDAPS certificate (docs/dev/p-certificate-services.md 2.1, decision 1):
/// TCP to <c>host:port</c> (636, or 3269 for a GC), the handshake only – no LDAP bind, no client certificate, no
/// application data. Implemented by <c>SslStreamTlsProbe</c> in <c>AdMng.Directory</c> (W7); run only on explicit
/// navigation or a button, at most 4 in parallel.
/// </summary>
public interface IDomainControllerTlsProbe
{
    /// <summary>Probes one port; never throws for a network failure (the outcome says what happened), honours <paramref name="cancellationToken"/>.</summary>
    /// <param name="host">The DC's FQDN (<c>HostNames.IsValid</c>; IP addresses are refused – SNI and the name check need a name).</param>
    /// <param name="port">636 or 3269.</param>
    /// <param name="timeout">The probe's own timeout (5 s): <see cref="TlsProbeOutcome.Timeout"/>.</param>
    /// <param name="cancellationToken">The caller's cancellation (propagates).</param>
    Task<TlsProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>What a probe saw.</summary>
public enum TlsProbeOutcome
{
    /// <summary>The handshake completed; the presented chain was captured.</summary>
    Ok,

    /// <summary>The host name does not resolve.</summary>
    NameNotResolved,

    /// <summary>The connection was refused.</summary>
    PortClosed,

    /// <summary>The handshake failed – AD DS resetting mid-handshake is <i>DC bez certyfikatu LDAPS</i>, not unreachable.</summary>
    HandshakeFailed,

    /// <summary>The probe's own timeout elapsed.</summary>
    Timeout,

    /// <summary>Not a valid host name (or an IP address), or a port other than 636 / 3269: nothing was contacted.</summary>
    InvalidHost,
}

/// <summary>The result of one probe.</summary>
/// <param name="Host">The host probed.</param>
/// <param name="Port">The port probed.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Protocol">The negotiated protocol (on <see cref="TlsProbeOutcome.Ok"/>).</param>
/// <param name="PresentedChain">DER, leaf first, as presented (not the locally built chain).</param>
/// <param name="PolicyErrors">What the platform validation reported (informational; the probe accepts every certificate).</param>
public sealed record TlsProbeResult(
    string Host,
    int Port,
    TlsProbeOutcome Outcome,
    SslProtocols? Protocol,
    IReadOnlyList<byte[]> PresentedChain,
    SslPolicyErrors? PolicyErrors)
{
    /// <summary>The port and the outcome only (never the host).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Port}: {Outcome}");
}

/// <summary>
/// The verdict on a DC's LDAPS certificate (<see cref="DcCertificateEvaluation.Evaluate"/>) – LDAPS only; the KDC
/// indicator is <see cref="KdcCertificateInfo"/>.
/// </summary>
/// <param name="Leaf">The presented leaf, null when it could not be parsed.</param>
/// <param name="NameMatchesDc">A SAN DNS entry, else the subject CN, equals the DC FQDN.</param>
/// <param name="NameMatchesDomain">The domain DNS name is in the SAN (<c>ldaps://&lt;domain&gt;</c>; a warning, not an error).</param>
/// <param name="HasServerAuth">EKU Server Authentication, or no EKU.</param>
/// <param name="IsTimeValid">Valid at the evaluation time.</param>
/// <param name="DaysLeft">Whole days to <c>NotAfter</c> (negative when expired).</param>
/// <param name="ChainsToAdRoot">Chains to a root of <c>CN=Certification Authorities</c> (custom root trust, nothing downloaded).</param>
/// <param name="IssuerInNtAuth">The issuer's certificate is in NTAuth.</param>
/// <param name="ChainStatus">The <c>X509ChainStatusFlags</c> names of the chain build.</param>
public sealed record DcCertificateInfo(
    CertificateSummary? Leaf,
    bool NameMatchesDc,
    bool NameMatchesDomain,
    bool HasServerAuth,
    bool IsTimeValid,
    int DaysLeft,
    bool ChainsToAdRoot,
    bool IssuerInNtAuth,
    IReadOnlyList<string> ChainStatus)
{
    /// <summary>The flags only (never a name or subject).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{nameof(DcCertificateInfo)}(name {NameMatchesDc}, server auth {HasServerAuth}, time {IsTimeValid}, chain {ChainsToAdRoot}, NTAuth {IssuerInNtAuth})");
}

/// <summary>One row of the DC LDAPS tab (W5) and the readiness report (W6).</summary>
/// <param name="Host">The DC's FQDN.</param>
/// <param name="Port">636 or 3269.</param>
/// <param name="Outcome">The probe's outcome.</param>
/// <param name="Certificate">The verdict on the presented certificate (on <see cref="TlsProbeOutcome.Ok"/>).</param>
public sealed record DcTlsCheck(string Host, int Port, TlsProbeOutcome Outcome, DcCertificateInfo? Certificate)
{
    /// <summary>Port and outcome only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{nameof(DcTlsCheck)}({Port}: {Outcome})");
}

/// <summary>Judges a presented LDAPS chain (pure; <c>now</c> is passed in so tests do not depend on the real clock).</summary>
public static class DcCertificateEvaluation
{
    /// <summary>
    /// Evaluates the leaf of <paramref name="presentedChain"/> for <paramref name="dcFqdn"/>: names, Server Authentication,
    /// validity at <paramref name="now"/>, the chain to an AD root (<c>X509Chain</c> with <c>CustomRootTrust</c>,
    /// <c>NoCheck</c>, <c>DisableCertificateDownloads</c>) and the issuer in NTAuth.
    /// </summary>
    public static DcCertificateInfo Evaluate(IReadOnlyList<byte[]> presentedChain, string dcFqdn, string domainDns, PkiTrustMaterial trust, DateTimeOffset now) =>
        throw new NotImplementedException("P1 C1: DcCertificateEvaluation.Evaluate");
}

/// <summary>The KDC readiness of a DC, from the certificates its object publishes (labelled <i>z certyfikatów opublikowanych w AD</i>).</summary>
public enum KdcReadinessState
{
    /// <summary>A time-valid certificate with KDC Authentication (or the legacy Smart-Card Logon + Server Authentication pair) whose issuer is in NTAuth.</summary>
    Ready,

    /// <summary>Published certificates, none with a KDC EKU.</summary>
    NoKdcEku,

    /// <summary>A KDC certificate whose issuer is not in NTAuth.</summary>
    IssuerNotInNtAuth,

    /// <summary>The KDC certificates are expired.</summary>
    Expired,

    /// <summary>Nothing published (publishing is optional: <i>nieznany</i>, not a failure).</summary>
    NoPublishedCertificate,
}

/// <summary>The KDC indicator of one DC.</summary>
/// <param name="State">The state.</param>
/// <param name="Best">The certificate the state is about, or null.</param>
public sealed record KdcCertificateInfo(KdcReadinessState State, CertificateSummary? Best)
{
    /// <summary>The state only.</summary>
    public override string ToString() => $"{nameof(KdcCertificateInfo)}({State})";
}

/// <summary>The KDC indicator (pure). The authoritative check of the KDC's own store is P4/K1.</summary>
public static class KdcCertificateEvaluation
{
    /// <summary>Evaluates the <c>userCertificate</c> values of a DC object at <paramref name="now"/>.</summary>
    public static KdcCertificateInfo Evaluate(IReadOnlyList<byte[]> publishedCertificates, string dcFqdn, PkiTrustMaterial trust, DateTimeOffset now) =>
        throw new NotImplementedException("P1 C1: KdcCertificateEvaluation.Evaluate");
}
