using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Client;

public enum WatchdogClientErrorKind
{
    NotConfigured, Unreachable, Timeout, CertificateInvalid, Unauthorized, Forbidden, NotFound,
    Validation, VersionConflict, ApiVersionMismatch, ServerError, Cancelled,

    /// <summary>The certificate does not chain to a trusted root and no pin is configured: the operator may confirm its
    /// fingerprint (<see cref="WatchdogClientException.Certificate"/>, stage B design 18 item 1).</summary>
    CertificateUntrusted,

    /// <summary>The certificate does not chain to a trusted root and differs from the pinned one
    /// (<see cref="WatchdogClientException.PinnedCertificateSha256"/>): never re-pinned without a new confirmation.</summary>
    CertificateChanged,
}

/// <summary>A failed watchdog API call, classified for the GUI (connection banner, validation issues, conflicts).</summary>
public sealed class WatchdogClientException : Exception
{
    public WatchdogClientException(WatchdogClientErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public WatchdogClientErrorKind Kind { get; }
    public int? StatusCode { get; init; }
    public IReadOnlyList<ConfigIssue> Issues { get; init; } = [];

    /// <summary><see cref="WatchdogClientErrorKind.CertificateUntrusted"/> / <see cref="WatchdogClientErrorKind.CertificateChanged"/>:
    /// the leaf the service presented.</summary>
    public WatchdogServerCertificate? Certificate { get; init; }

    /// <summary><see cref="WatchdogClientErrorKind.CertificateChanged"/>: the pin in effect (64 hex digits).</summary>
    public string? PinnedCertificateSha256 { get; init; }
}
