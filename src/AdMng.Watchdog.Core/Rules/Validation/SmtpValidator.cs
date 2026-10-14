using System.Text.RegularExpressions;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>
/// Section <c>smtp</c> (8.3): <c>required</c> (host and from when enabled, Basic login, OAuth identity),
/// <c>invalidHostName</c>, <c>invalidAddress</c>, <c>insecureCredentials</c>, <c>oauthHostNotAllowed</c>,
/// <c>acceptAnyNotAllowed</c>, <c>pinnedHash</c> and the warnings <c>exchangeOnlineBasic</c>, <c>noTls</c>,
/// <c>revocationOff</c> (only when SMTP is enabled).
/// </summary>
internal static partial class SmtpValidator
{
    private static readonly string[] ExchangeOnlineHosts = ["smtp.office365.com", "smtp-legacy.office365.com"];

    public static void Validate(ValidationRun run)
    {
        var smtp = run.Configuration.Smtp;
        if (smtp.Enabled)
        {
            Required(run, "smtp.host", smtp.Host, "The SMTP host is required when e-mail is enabled.");
            Required(run, "smtp.from", smtp.From, "The sender address is required when e-mail is enabled.");
        }

        HostName(run, "smtp.host", smtp.Host);
        HostName(run, "smtp.localDomain", smtp.LocalDomain);
        Address(run, "smtp.from", smtp.From);
        Address(run, "smtp.replyTo", smtp.ReplyTo);

        if (smtp.Authentication != SmtpAuthentication.None && smtp.Security is not (SmtpSecurity.StartTls or SmtpSecurity.SslOnConnect))
        {
            run.Error("smtp.security", ConfigValidationCodes.InsecureCredentials,
                "Credentials are sent only over TLS – use StartTls or SslOnConnect.");
        }

        switch (smtp.Authentication)
        {
            case SmtpAuthentication.Basic:
                Basic(run, smtp);
                break;
            case SmtpAuthentication.OAuth2ClientCredentials:
                OAuth(run, smtp);
                break;
        }

        if (smtp.CertificateValidation == CertificateValidationMode.AcceptAnyDevelopmentOnly && !run.Context.IsDevelopment)
        {
            run.Error("smtp.certificateValidation", ConfigValidationCodes.AcceptAnyNotAllowed,
                "Accepting any server certificate is allowed only in the Development environment.");
        }

        if (smtp.CertificateValidation == CertificateValidationMode.PinnedSha256 && !Sha256Hex().IsMatch(smtp.PinnedSha256 ?? string.Empty))
        {
            run.Error("smtp.pinnedSha256", ConfigValidationCodes.PinnedHash, "The pinned certificate hash must be 64 hexadecimal characters (SHA-256).");
        }

        if (smtp.Enabled)
        {
            Warnings(run, smtp);
        }
    }

    private static void Basic(ValidationRun run, SmtpSettings smtp)
    {
        if (!smtp.Enabled)
        {
            return;
        }

        Required(run, "smtp.userName", smtp.UserName, "Basic authentication needs a user name.");
        if (!SecretPresence.HasValue(smtp.Password, SecretNames.SmtpPassword, run.Context))
        {
            run.Error("smtp.password", ConfigValidationCodes.Required, "Basic authentication needs a password.");
        }
    }

    private static void OAuth(ValidationRun run, SmtpSettings smtp)
    {
        if (smtp.Enabled)
        {
            var oauth = smtp.OAuth;
            OAuthIdentity.Validate(run, "smtp.oAuth", oauth.TenantId, oauth.ClientId, oauth.CertificateThumbprint,
                oauth.ClientSecret, SecretNames.SmtpOAuthClientSecret);
            Required(run, "smtp.userName", smtp.UserName, "OAuth2 needs the sender mailbox as user name.");
        }

        var host = smtp.Host?.Trim().TrimEnd('.');
        if (!string.IsNullOrEmpty(host) && !run.Context.OAuthSmtpHosts.Any(h => string.Equals(h?.Trim().TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase)))
        {
            run.Error("smtp.host", ConfigValidationCodes.OAuthHostNotAllowed,
                "OAuth2 tokens are sent only to the hosts of the bootstrap setting Notifications.OAuthSmtpHosts.");
        }
    }

    private static void Warnings(ValidationRun run, SmtpSettings smtp)
    {
        var host = smtp.Host?.Trim().TrimEnd('.');
        if (smtp.Authentication == SmtpAuthentication.Basic
            && ExchangeOnlineHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)))
        {
            run.Warning("smtp.authentication", ConfigValidationCodes.ExchangeOnlineBasic,
                "Exchange Online disables Basic SMTP AUTH by default from the end of December 2026 – use OAuth2.");
        }

        if (smtp.Security is SmtpSecurity.None or SmtpSecurity.StartTlsWhenAvailable)
        {
            run.Warning("smtp.security", ConfigValidationCodes.NoTls,
                "Without mandatory TLS an attacker on the network could read or alter alerts.");
        }

        if (!smtp.CheckRevocation)
        {
            run.Warning("smtp.checkRevocation", ConfigValidationCodes.RevocationOff, "Certificate revocation is not checked.");
        }
    }

    private static void Required(ValidationRun run, string path, string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            run.Error(path, ConfigValidationCodes.Required, message);
        }
    }

    private static void HostName(ValidationRun run, string path, string? value)
    {
        if (value is not null && !HostNameSyntax().IsMatch(value))
        {
            run.Error(path, ConfigValidationCodes.InvalidHostName, "Host names use letters, digits, '.' and '-' only.");
        }
    }

    private static void Address(ValidationRun run, string path, string? value)
    {
        if (value is not null && !EmailAddressRules.IsValid(value))
        {
            run.Error(path, ConfigValidationCodes.InvalidAddress, "Not a valid e-mail address.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9.-]{1,253}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HostNameSyntax();

    [GeneratedRegex("^[0-9A-Fa-f]{64}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Sha256Hex();
}
