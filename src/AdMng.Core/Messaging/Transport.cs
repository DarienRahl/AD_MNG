using System.Net;

namespace AdMng.Core.Messaging;

/// <summary>Which transport sends the messages.</summary>
public enum UserMessageTransportKind
{
    /// <summary>Directly from AD Manager to an SMTP server (AdMng.Mail).</summary>
    DirectSmtp,

    /// <summary>Through the AdMng Watchdog service (README U1b).</summary>
    Watchdog,
}

/// <summary>
/// What a transport accepts (U1c, design note section 9.6): inline images and their limits, the longest HTML body and, for
/// the AdMng Watchdog, how many bytes of images its outbox may still hold. Direct SMTP has the Core limits; an AdMng
/// Watchdog without the U1c contract has <see cref="Legacy"/>.
/// </summary>
public sealed record UserMessageTransportCapabilities
{
    public bool InlineImages { get; init; }

    public int MaxInlineImages { get; init; }

    public long MaxInlineImageBytes { get; init; }

    public long MaxInlineImagesTotalBytes { get; init; }

    public int MaxHtmlLength { get; init; } = Html.MessageHtmlLimits.LegacyMaxHtmlLength;

    /// <summary>Bytes of images the service's outbox may still take (null: no such limit, e.g. direct SMTP).</summary>
    public long? ImageStorageAvailableBytes { get; init; }

    /// <summary>Direct SMTP: the Core limits.</summary>
    public static UserMessageTransportCapabilities Direct { get; } = new()
    {
        InlineImages = true,
        MaxInlineImages = Images.MessageImageLimits.MaxImagesPerMessage,
        MaxInlineImageBytes = Images.MessageImageLimits.MaxImageBytes,
        MaxInlineImagesTotalBytes = Images.MessageImageLimits.MaxImagesBytesPerMessage,
        MaxHtmlLength = Html.MessageHtmlLimits.MaxHtmlLength,
    };

    /// <summary>An AdMng Watchdog without the U1c contract: no images, HTML up to 100 000 characters.</summary>
    public static UserMessageTransportCapabilities Legacy { get; } = new();
}

/// <summary>Whether a transport can be used now; <see cref="Reason"/> is a code (localization key suffix) when not.</summary>
public sealed record UserMessageTransportStatus(bool IsAvailable, string? Reason = null)
{
    public static UserMessageTransportStatus Available { get; } = new(true);

    /// <summary>What the transport accepts (U1c); direct SMTP by default.</summary>
    public UserMessageTransportCapabilities Capabilities { get; init; } = UserMessageTransportCapabilities.Direct;

    public const string NotConfigured = "notConfigured";
    public const string NotSupported = "notSupported";
    public const string Unreachable = "unreachable";

    /// <summary>The transport works but the feature is switched off (AdMng Watchdog: <c>userMessages.enabled</c>).</summary>
    public const string Disabled = "disabled";

    /// <summary>The caller may not use the transport (AdMng Watchdog: no Admin/Viewer role, or denied).</summary>
    public const string Forbidden = "forbidden";
}

/// <summary>Outcome of one send attempt.</summary>
public enum UserMessageDeliveryOutcome
{
    /// <summary>The server accepted the message for the To recipients.</summary>
    Accepted,

    /// <summary>Permanently rejected (5xx) – only this message fails.</summary>
    Rejected,

    /// <summary>Temporary failure (4xx, timeout, connection lost) – retried on a new connection.</summary>
    Transient,

    /// <summary>The server refused the authentication – every further message would fail too.</summary>
    AuthenticationFailed,

    /// <summary>TLS, certificate or configuration failure – every further message would fail too.</summary>
    TransportFailed,
}

/// <summary>
/// Result of one send attempt: outcome, a code in the Watchdog's format (<c>smtp.550.5.1.1</c>, <c>auth</c>, <c>tls</c>,
/// <c>timeout</c>, <c>socket.*</c>, <c>io</c>), the basic SMTP status and a hint key (<c>DeliveryHints</c> of
/// AdMng.Watchdog.Contracts). Never the server's reply text (it may contain addresses).
/// </summary>
public sealed record UserMessageDelivery(UserMessageDeliveryOutcome Outcome, string Code, int? SmtpStatus = null, string? Hint = null)
{
    /// <summary>CC addresses the server rejected while it accepted the To recipients (shown as a warning).</summary>
    public IReadOnlyList<string> RejectedCopies { get; init; } = [];

    public bool IsAccepted => Outcome == UserMessageDeliveryOutcome.Accepted;

    /// <summary>The rest of a run would fail the same way (authentication, TLS, configuration).</summary>
    public bool StopsRun => Outcome is UserMessageDeliveryOutcome.AuthenticationFailed or UserMessageDeliveryOutcome.TransportFailed;

    public static UserMessageDelivery Accepted(int? status = 250) => new(UserMessageDeliveryOutcome.Accepted, "ok", status);
}

/// <summary>A failure of <see cref="IUserMessageTransport.ConnectAsync"/>, classified.</summary>
public sealed class UserMessageTransportException : Exception
{
    public UserMessageTransportException(UserMessageDelivery delivery, Exception? innerException = null)
        : base($"The message transport failed ({delivery?.Code}).", innerException)
    {
        Delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
    }

    public UserMessageDelivery Delivery { get; }
}

/// <summary>
/// A way to send messages to users (README U1, design note section 7). Implementations: direct SMTP (AdMng.Mail) and the
/// AdMng Watchdog service (U1b). The dispatcher above it does rate limiting, retries and the report; a transport sends one
/// already rendered and checked message at a time and classifies failures.
/// </summary>
public interface IUserMessageTransport
{
    UserMessageTransportKind Kind { get; }

    /// <summary>Where the messages go, for the window and the session log ("smtp.contoso.com:587").</summary>
    string Description { get; }

    /// <summary>Whether the transport can be used (configured, reachable, supported by the service).</summary>
    Task<UserMessageTransportStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Opens a connection for a series of messages.</summary>
    /// <exception cref="UserMessageTransportException">Connecting, TLS or authentication failed (classified).</exception>
    Task<IUserMessageConnection> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>An open connection of a transport (one SMTP session, one Watchdog API client).</summary>
public interface IUserMessageConnection : IAsyncDisposable
{
    /// <summary>Sends one message. Failures are returned as a classified <see cref="UserMessageDelivery"/>, not thrown
    /// (except cancellation).</summary>
    Task<UserMessageDelivery> SendAsync(OutgoingUserMessage message, CancellationToken cancellationToken);
}

/// <summary>TLS of the direct SMTP connection.</summary>
public enum SmtpTlsMode
{
    /// <summary>STARTTLS on the submission port; the connection fails when the server does not offer it (default).</summary>
    StartTlsRequired,

    /// <summary>TLS from the first byte (port 465).</summary>
    ImplicitTls,

    /// <summary>STARTTLS when the server offers it, else plain text (needs a confirmed warning).</summary>
    StartTlsWhenAvailable,

    /// <summary>Plain text (needs a confirmed warning).</summary>
    None,
}

/// <summary>How the SMTP server's certificate is checked. There is deliberately no "accept any".</summary>
public enum SmtpCertificateCheck
{
    /// <summary>The system's trusted roots, name and revocation (default).</summary>
    SystemTrust,

    /// <summary>The leaf certificate's SHA-256 must equal the pin.</summary>
    PinnedSha256,
}

/// <summary>Authentication to the SMTP server.</summary>
public enum SmtpAuthentication
{
    /// <summary>None (a relay that accepts the sender's host).</summary>
    None,

    /// <summary>The current Windows account: SASL GSSAPI (Kerberos), else NTLM, where the server offers it.</summary>
    CurrentWindowsAccount,

    /// <summary>User name and password (asked when needed, kept in memory only for the session).</summary>
    Basic,
}

/// <summary>The direct SMTP connection (settings, never a password).</summary>
public sealed record DirectSmtpOptions
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 587;

    public SmtpTlsMode Tls { get; init; } = SmtpTlsMode.StartTlsRequired;

    public SmtpCertificateCheck Certificate { get; init; } = SmtpCertificateCheck.SystemTrust;

    /// <summary>SHA-256 (hex, 64 characters, separators ignored) for <see cref="SmtpCertificateCheck.PinnedSha256"/>.</summary>
    public string? PinnedSha256 { get; init; }

    public SmtpAuthentication Authentication { get; init; } = SmtpAuthentication.None;

    /// <summary>User name of <see cref="SmtpAuthentication.Basic"/>.</summary>
    public string? UserName { get; init; }

    /// <summary>EHLO / Message-ID domain; null = the machine's FQDN.</summary>
    public string? LocalDomain { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>"host:port".</summary>
    public string Description => $"{Host}:{Port}";

    /// <summary>A certificate pin in its comparable form (64 upper-case hex digits; separators ignored), or null when it is not one.</summary>
    public static string? NormalizePin(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return null;
        }

        var hex = new string([.. pin.Where(char.IsAsciiHexDigit)]);
        return hex.Length == 64 ? hex.ToUpperInvariant() : null;
    }
}

/// <summary>Stage of a connection test.</summary>
public enum SmtpTestStage
{
    Connect,
    Tls,
    Authenticate,
    Done,
}

/// <summary>Result of <see cref="IDirectSmtpTransport.TestConnectionAsync"/>.</summary>
public sealed record SmtpConnectionTest
{
    public bool Success { get; init; }

    /// <summary>The stage that failed (or <see cref="SmtpTestStage.Done"/>).</summary>
    public SmtpTestStage Stage { get; init; }

    public UserMessageDelivery? Failure { get; init; }

    /// <summary>The connection is encrypted (STARTTLS or implicit TLS).</summary>
    public bool IsEncrypted { get; init; }

    public bool StartTlsOffered { get; init; }

    /// <summary>"Tls13" …, when encrypted.</summary>
    public string? TlsProtocol { get; init; }

    /// <summary>SHA-256 of the server certificate (for pinning), when TLS was used.</summary>
    public string? CertificateSha256 { get; init; }

    /// <summary>SASL mechanisms the server offered after TLS.</summary>
    public IReadOnlyList<string> AuthenticationMechanisms { get; init; } = [];

    /// <summary>The mechanism used to authenticate, if any.</summary>
    public string? AuthenticatedWith { get; init; }
}

/// <summary>The direct SMTP transport, with a connection test.</summary>
public interface IDirectSmtpTransport : IUserMessageTransport
{
    /// <summary>Connects, negotiates TLS and authenticates without sending anything.</summary>
    Task<SmtpConnectionTest> TestConnectionAsync(CancellationToken cancellationToken);
}

/// <summary>Creates direct SMTP transports (implemented by AdMng.Mail; the app registers it).</summary>
public interface IDirectSmtpTransportFactory
{
    /// <param name="options">The connection.</param>
    /// <param name="credential">
    /// For <see cref="SmtpAuthentication.Basic"/>: user name and password from the session memory (never persisted); null
    /// otherwise.
    /// </param>
    IDirectSmtpTransport Create(DirectSmtpOptions options, NetworkCredential? credential);
}
