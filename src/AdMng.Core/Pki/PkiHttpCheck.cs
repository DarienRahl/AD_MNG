using System.Globalization;

namespace AdMng.Core.Pki;

/// <summary>What an HTTP URL of a certificate is for (docs/dev/p-certificate-services.md 2.4, 2.5).</summary>
public enum PkiUrlKind
{
    /// <summary>AIA CA Issuers: the issuer's certificate.</summary>
    AiaCaIssuers,

    /// <summary>CRL distribution point: the base CRL.</summary>
    Cdp,

    /// <summary>The delta CRL location (freshest CRL).</summary>
    DeltaCdp,

    /// <summary>AIA OCSP responder (reachability only in P1).</summary>
    Ocsp,
}

/// <summary>An <c>http</c>/<c>https</c> URL read from a certificate, to be checked on the operator's button.</summary>
/// <param name="Url">The URL as stored in the certificate.</param>
/// <param name="Kind">What it is for.</param>
public sealed record PkiUrl(string Url, PkiUrlKind Kind)
{
    /// <summary>The kind only (never the URL).</summary>
    public override string ToString() => $"{nameof(PkiUrl)}({Kind})";
}

/// <summary>The outcome of one HTTP check (<c>Pki.Http.&lt;PkiHttpOutcome&gt;</c>).</summary>
public enum PkiHttpOutcome
{
    /// <summary>Fetched and parsed (AIA: a certificate, CDP: a CRL; OCSP: any HTTP response).</summary>
    Ok,

    /// <summary>HTTP 404.</summary>
    NotFound,

    /// <summary>Another HTTP error status (or more than 3 redirects).</summary>
    HttpError,

    /// <summary>15 s per URL elapsed (including the body).</summary>
    Timeout,

    /// <summary>The body is larger than 20 MB.</summary>
    TooLarge,

    /// <summary>The body is not a certificate (AIA) or a CRL (CDP).</summary>
    InvalidContent,

    /// <summary>The HTTPS handshake failed.</summary>
    TlsError,

    /// <summary>The host name does not resolve.</summary>
    NameNotResolved,

    /// <summary>The connection failed.</summary>
    ConnectFailed,

    /// <summary>Not contacted: not an absolute <c>http</c>/<c>https</c> URL (<c>file:</c>, UNC, <c>ldap://host</c>), or a redirect to one.</summary>
    NotChecked,
}

/// <summary>The result of one HTTP check; bodies are parsed in memory and dropped, the summaries kept until F5.</summary>
/// <param name="Url">The URL checked (as read from the certificate).</param>
/// <param name="Kind">What it is for.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="StatusCode">The final HTTP status code, null without a response.</param>
/// <param name="Certificate">The AIA certificate fetched.</param>
/// <param name="Crl">The CDP CRL fetched.</param>
public sealed record PkiHttpCheckResult(string Url, PkiUrlKind Kind, PkiHttpOutcome Outcome, int? StatusCode, CertificateSummary? Certificate, CrlSummary? Crl)
{
    /// <summary>Kind, outcome and status only (never the URL).</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(PkiHttpCheckResult)}({Kind}: {Outcome}{(StatusCode is { } status ? " " + status.ToString(CultureInfo.InvariantCulture) : string.Empty)})");
}
