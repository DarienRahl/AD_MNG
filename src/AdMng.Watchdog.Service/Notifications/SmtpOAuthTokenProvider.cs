using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// The XOAUTH2 token for Exchange Online SMTP (spec 15.6): only for a host in the bootstrap
/// <c>Notifications.OAuthSmtpHosts</c>, only with a client secret whose binding matches (or a certificate), authority and
/// scope fixed by <see cref="EntraCloud"/> (<c>EntraCloudInfo.SmtpScope</c>). Nothing is requested when a check fails.
/// </summary>
public sealed class SmtpOAuthTokenProvider(IEntraTokenClient tokens, DestinationGuard guard)
{
    public async Task<AccessTokenResult> AcquireAsync(SmtpSettings smtp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        if (!guard.IsOAuthSmtpHostAllowed(smtp))
        {
            return AccessTokenResult.Refused("oauthHostNotAllowed");
        }

        var oauth = smtp.OAuth ?? new SmtpOAuthSettings();
        if (!Guid.TryParse(oauth.TenantId, out _) || !Guid.TryParse(oauth.ClientId, out _))
        {
            return AccessTokenResult.Refused("oauth.notConfigured");
        }

        Core.Model.SecretValue? secret = null;
        if (string.IsNullOrWhiteSpace(oauth.CertificateThumbprint))
        {
            var guarded = await guard.SmtpOAuthClientSecretAsync(smtp, cancellationToken).ConfigureAwait(false);
            if (!guarded.IsAvailable)
            {
                return AccessTokenResult.Refused(guarded.FailureCode ?? "secret.missing", guarded.Hint);
            }

            secret = guarded.Value;
        }

        try
        {
            var token = await tokens.AcquireTokenAsync(
                new EntraTokenRequest
                {
                    Cloud = oauth.Cloud,
                    TenantId = oauth.TenantId!,
                    ClientId = oauth.ClientId!,
                    Scope = EntraCloudInfo.SmtpScope(oauth.Cloud),
                    CertificateThumbprint = oauth.CertificateThumbprint,
                    ClientSecret = secret,
                },
                cancellationToken).ConfigureAwait(false);
            return new AccessTokenResult { Token = token };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return AccessTokenResult.Failed(ex);
        }
    }
}
