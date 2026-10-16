using System.Security.Cryptography.X509Certificates;

namespace AdMng.Watchdog.Testing.Smtp;

/// <summary>Configuration of a <see cref="SmtpSink"/>.</summary>
public sealed record SmtpSinkOptions
{
    public SmtpSinkSecurity Security { get; init; } = SmtpSinkSecurity.Plain;

    /// <summary>Server certificate for <see cref="SmtpSinkSecurity.StartTls"/> / <see cref="SmtpSinkSecurity.ImplicitTls"/>
    /// (default: <c>TestCertificates.Localhost</c>).</summary>
    public X509Certificate2? Certificate { get; init; }

    /// <summary>Require AUTH (offered only after TLS – the sink never allows AUTH in clear text).</summary>
    public bool RequireAuthentication { get; init; }

    /// <summary>Accepted credentials (user → password); AUTH PLAIN / LOGIN.</summary>
    public IReadOnlyDictionary<string, string> Accounts { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>SIZE limit in bytes (0 = none).</summary>
    public int MaxMessageSize { get; init; }
}
