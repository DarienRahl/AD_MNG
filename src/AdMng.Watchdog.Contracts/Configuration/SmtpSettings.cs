namespace AdMng.Watchdog.Contracts.Configuration;

public enum SmtpSecurity { None, StartTls, StartTlsWhenAvailable, SslOnConnect }
public enum SmtpAuthentication { None, Basic, OAuth2ClientCredentials }
public enum CertificateValidationMode { System, PinnedSha256, AcceptAnyDevelopmentOnly }

/// <summary>Entra cloud – fixes the token authority host and the SMTP scope (no free-text authority/scope: an
/// attacker-chosen authority would receive the client assertion or secret).
/// Public: login.microsoftonline.com, https://outlook.office365.com/.default;
/// UsGovernment: login.microsoftonline.us, https://outlook.office365.us/.default;
/// China: login.partner.microsoftonline.cn, https://partner.outlook.cn/.default.</summary>
public enum EntraCloud { Public, UsGovernment, China }

/// <summary>Exchange Online OAuth2 client credentials (XOAUTH2).</summary>
public sealed record SmtpOAuthSettings
{
    public EntraCloud Cloud { get; init; } = EntraCloud.Public;
    public string? TenantId { get; init; }                // GUID
    public string? ClientId { get; init; }                // GUID
    public string? CertificateThumbprint { get; init; }   // LocalMachine\My, private key readable by the gMSA (preferred)
    public SecretField ClientSecret { get; init; } = new();
}

/// <summary>The e-mail channel (immediate alerts and the digest).</summary>
public sealed record SmtpSettings
{
    public bool Enabled { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; } = 587;
    public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;   // also for relays on port 25
    public SmtpAuthentication Authentication { get; init; } = SmtpAuthentication.None;
    public string? UserName { get; init; }                // Basic: login; OAuth2: sender mailbox (XOAUTH2 user=)
    public SecretField Password { get; init; } = new();
    public SmtpOAuthSettings OAuth { get; init; } = new();
    public string? From { get; init; }                    // "AD Watchdog <watchdog@contoso.com>"
    public string? ReplyTo { get; init; }
    public string? LocalDomain { get; init; }             // EHLO + Message-ID domain (hostname syntax); null = machine FQDN
    public CertificateValidationMode CertificateValidation { get; init; } = CertificateValidationMode.System;
    public string? PinnedSha256 { get; init; }            // hex SHA-256 of the server certificate
    public bool CheckRevocation { get; init; } = true;
    public int MaxMessagesPerMinute { get; init; } = 20;
    public int MaxRecipientsPerMessage { get; init; } = 50;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}
