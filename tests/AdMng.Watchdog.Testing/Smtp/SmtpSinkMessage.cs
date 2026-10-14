using System.Text;

namespace AdMng.Watchdog.Testing.Smtp;

/// <summary>One message received by <see cref="SmtpSink"/> (raw RFC 5322 bytes; parse with MimeKit in the tests).</summary>
public sealed record SmtpSinkMessage
{
    public string? MailFrom { get; init; }

    public IReadOnlyList<string> Recipients { get; init; } = [];

    public required byte[] Data { get; init; }

    /// <summary>The session was protected by TLS when the message was sent.</summary>
    public bool Secure { get; init; }

    /// <summary>The authenticated user of the session (null = no AUTH).</summary>
    public string? AuthenticatedUser { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public string Text => Encoding.UTF8.GetString(Data);
}
