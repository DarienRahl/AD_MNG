using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Formatting.Webhooks;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Secrets;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// Runs before any credential is touched (spec 15.6, 15.7, 18): a stored secret is released only when the binding it
/// was stored with equals <see cref="SecretBindings"/> of the effective settings, XOAUTH2 only to the bootstrap
/// <c>Notifications.OAuthSmtpHosts</c>, a Teams bearer token only to Power Platform workflow URLs. Unsaved settings
/// (test sends) use their own <see cref="SecretField.NewValue"/>; a stored secret only with a matching binding.
/// </summary>
public sealed class DestinationGuard
{
    /// <summary>Failure code of a secret stored for another destination.</summary>
    public const string RebindRequired = "secret.rebindRequired";

    private readonly ISecretStore _secrets;
    private readonly IOptionsMonitor<ServiceOptions> _options;

    public DestinationGuard(ISecretStore secrets, IOptionsMonitor<ServiceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(options);
        _secrets = secrets;
        _options = options;
    }

    /// <summary>The SMTP Basic password for <paramref name="smtp"/>.</summary>
    public Task<GuardedSecret> SmtpPasswordAsync(SmtpSettings smtp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        return ReleaseAsync(SecretNames.SmtpPassword, smtp.Password, SecretBindings.ForSmtpPassword(smtp), cancellationToken);
    }

    /// <summary>True when <paramref name="smtp"/>'s host may receive an XOAUTH2 token.</summary>
    public bool IsOAuthSmtpHostAllowed(SmtpSettings smtp)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        var host = NormalizeHost(smtp.Host);
        return host is not null && _options.CurrentValue.Notifications.EffectiveOAuthSmtpHosts.Any(h => string.Equals(NormalizeHost(h), host, StringComparison.Ordinal));
    }

    /// <summary>The OAuth client secret for the SMTP token (after the host allow-list).</summary>
    public Task<GuardedSecret> SmtpOAuthClientSecretAsync(SmtpSettings smtp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        if (!IsOAuthSmtpHostAllowed(smtp))
        {
            return Task.FromResult(GuardedSecret.Refused("oauthHostNotAllowed"));
        }

        return ReleaseAsync(SecretNames.SmtpOAuthClientSecret, smtp.OAuth?.ClientSecret, SecretBindings.ForSmtpOAuthClientSecret(smtp), cancellationToken);
    }

    /// <summary>The channel URL (Teams workflow URL / webhook URL – a credential itself).</summary>
    public Task<GuardedSecret> ChannelUrlAsync(ChannelSettings channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return ReleaseAsync(SecretNames.ChannelUrl(channel.Id), channel.Url, SecretBindings.ForChannelUrl(channel), cancellationToken);
    }

    /// <summary>The Teams OAuth client secret – only for a Power Platform workflow URL.</summary>
    public Task<GuardedSecret> TeamsClientSecretAsync(ChannelSettings channel, string url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!TeamsUrlValidator.Validate(url, TeamsAuthMode.EntraClientCredentials).IsValid)
        {
            return Task.FromResult(GuardedSecret.Refused("teams.hostNotAllowed"));
        }

        return ReleaseAsync(
            SecretNames.ChannelClientSecret(channel.Id),
            channel.Teams?.ClientSecret,
            SecretBindings.ForTeamsClientSecret(channel, SecretBindings.UrlHost(url)),
            cancellationToken);
    }

    /// <summary>True when a Teams bearer token may be sent to <paramref name="url"/>.</summary>
    public static bool IsTeamsTokenDestination(string url) => TeamsUrlValidator.Validate(url, TeamsAuthMode.EntraClientCredentials).IsValid;

    /// <summary>A webhook signing secret (current or previous) bound to the URL host.</summary>
    public Task<GuardedSecret> WebhookSigningSecretAsync(ChannelSettings channel, string url, bool previous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var webhook = channel.Webhook ?? new WebhookChannelOptions();
        return ReleaseAsync(
            previous ? SecretNames.WebhookPreviousSigningSecret(channel.Id) : SecretNames.WebhookSigningSecret(channel.Id),
            previous ? webhook.PreviousSigningSecret : webhook.SigningSecret,
            SecretBindings.ForWebhookSigningSecret(channel, SecretBindings.UrlHost(url)),
            cancellationToken);
    }

    private async Task<GuardedSecret> ReleaseAsync(string name, SecretField? field, string expectedBinding, CancellationToken cancellationToken)
    {
        if (field is { NewValue: { Length: > 0 } newValue })
        {
            return GuardedSecret.Released(new SecretValue(newValue));   // entered for exactly these (unsaved) settings
        }

        if (field?.Clear == true)
        {
            return GuardedSecret.Refused("secret.missing");
        }

        StoredSecret? stored;
        try
        {
            stored = await _secrets.GetAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (SecretUnreadableException)
        {
            return GuardedSecret.Refused(SecretUnreadableException.HealthCode);
        }

        if (stored is null)
        {
            return GuardedSecret.Refused("secret.missing");
        }

        return string.Equals(stored.Binding, expectedBinding, StringComparison.Ordinal)
            ? GuardedSecret.Released(stored.Value)
            : GuardedSecret.Refused(RebindRequired, DeliveryHints.SecretRebindRequired);
    }

    private static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var trimmed = host.Trim().TrimEnd('.');
        try
        {
            return new System.Globalization.IdnMapping().GetAscii(trimmed).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return trimmed.ToLowerInvariant();
        }
    }
}
