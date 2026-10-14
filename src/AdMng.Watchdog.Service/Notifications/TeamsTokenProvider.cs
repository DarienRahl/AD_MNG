using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// The bearer token for Power Automate triggers restricted to "Specific users in my tenant" (spec 15.7): only for a
/// Power Platform workflow URL, only a documented scope of the channel's cloud (<c>EntraCloudInfo.TeamsScopes</c>), only
/// with a client secret whose binding matches (or a certificate). Nothing is requested when a check fails.
/// </summary>
public sealed class TeamsTokenProvider(IEntraTokenClient tokens, DestinationGuard guard)
{
    public async Task<AccessTokenResult> AcquireAsync(ChannelSettings channel, string url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!DestinationGuard.IsTeamsTokenDestination(url))
        {
            return AccessTokenResult.Refused("teams.hostNotAllowed");
        }

        var teams = channel.Teams ?? new TeamsChannelOptions();
        if (!EntraCloudInfo.TeamsScopes(teams.Cloud).Contains(teams.Scope, StringComparer.Ordinal))
        {
            return AccessTokenResult.Refused("invalidScope");
        }

        if (!Guid.TryParse(teams.TenantId, out _) || !Guid.TryParse(teams.ClientId, out _))
        {
            return AccessTokenResult.Refused("oauth.notConfigured");
        }

        Core.Model.SecretValue? secret = null;
        if (string.IsNullOrWhiteSpace(teams.CertificateThumbprint))
        {
            var guarded = await guard.TeamsClientSecretAsync(channel, url, cancellationToken).ConfigureAwait(false);
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
                    Cloud = teams.Cloud,
                    TenantId = teams.TenantId!,
                    ClientId = teams.ClientId!,
                    Scope = teams.Scope,
                    CertificateThumbprint = teams.CertificateThumbprint,
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
