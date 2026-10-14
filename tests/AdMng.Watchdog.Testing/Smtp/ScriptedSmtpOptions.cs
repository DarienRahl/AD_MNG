using System.Security.Cryptography.X509Certificates;

namespace AdMng.Watchdog.Testing.Smtp;

/// <summary>Configuration of a <see cref="ScriptedSmtpServer"/>.</summary>
public sealed record ScriptedSmtpOptions
{
    /// <summary>EHLO extension lines (without the "250-" prefix), e.g. "AUTH PLAIN LOGIN XOAUTH2", "8BITMIME", "SIZE 10485760".
    /// "STARTTLS" is added automatically when <see cref="StartTlsCertificate"/> is set.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = ["AUTH PLAIN LOGIN XOAUTH2", "8BITMIME", "ENHANCEDSTATUSCODES"];

    /// <summary>Offer STARTTLS with this certificate.</summary>
    public X509Certificate2? StartTlsCertificate { get; init; }

    /// <summary>TLS from the first byte (465) with this certificate.</summary>
    public X509Certificate2? ImplicitTlsCertificate { get; init; }
}
