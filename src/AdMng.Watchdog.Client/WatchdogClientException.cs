using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Client;

public enum WatchdogClientErrorKind
{
    NotConfigured, Unreachable, Timeout, CertificateInvalid, Unauthorized, Forbidden, NotFound,
    Validation, VersionConflict, ApiVersionMismatch, ServerError, Cancelled,
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
}
