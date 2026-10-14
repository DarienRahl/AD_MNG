using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Formatting.Webhooks;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>
/// Teams and webhook channels (8.3): <c>required</c> (URL when enabled, webhook signing secret, Teams OAuth identity),
/// <c>insecureUrl</c>, <c>retiredTeamsUrl</c>, <c>teamsUrlFormat</c> (error for EntraClientCredentials, warning for a
/// custom SAS host – <see cref="TeamsUrlValidator"/>), <c>invalidScope</c>, <c>hostNotAllowed</c> and the channel
/// ranges (rate limits ≥ 1, heartbeat ≥ 5 min). A new URL is checked in full; a stored URL only through the host its
/// hint declares (the full URL was checked when it was entered).
/// </summary>
internal static class ChannelValidator
{
    public static void Validate(ValidationRun run)
    {
        var channels = run.Configuration.Channels;
        for (var i = 0; i < channels.Count; i++)
        {
            if (channels[i] is { } channel)
            {
                Channel(run, ValidationRun.Item("channels", i), channel);
            }
        }
    }

    private static void Channel(ValidationRun run, string path, ChannelSettings channel)
    {
        RangeCheck.Check(run, path + ".rateLimit.perSecond", channel.RateLimit.PerSecond, 1, 1_000_000);
        RangeCheck.Check(run, path + ".rateLimit.perMinute", channel.RateLimit.PerMinute, 1, 1_000_000);
        RangeCheck.Check(run, path + ".rateLimit.perHour", channel.RateLimit.PerHour, 1, 1_000_000);

        if (channel.Enabled && !SecretPresence.HasValue(channel.Url, SecretNames.ChannelUrl(channel.Id), run.Context))
        {
            run.Error(path + ".url", ConfigValidationCodes.Required, "The channel URL is required when the channel is enabled.");
        }

        switch (channel.Type)
        {
            case ChannelType.Teams:
                Teams(run, path, channel, channel.Teams ?? new TeamsChannelOptions());
                break;
            case ChannelType.Webhook:
                Webhook(run, path, channel, channel.Webhook ?? new WebhookChannelOptions());
                break;
        }
    }

    private static void Teams(ValidationRun run, string path, ChannelSettings channel, TeamsChannelOptions teams)
    {
        if (!string.IsNullOrEmpty(channel.Url.NewValue))
        {
            var result = TeamsUrlValidator.Validate(channel.Url.NewValue, teams.AuthMode, run.Context.IsDevelopment);
            if (!result.IsValid && result.Code is { } code && result.Severity is { } severity)
            {
                Add(run, path + ".url", code, severity, result.Message ?? "Invalid Teams URL.");
            }
        }
        else if (channel.Url.Hint is { Length: > 0 } hint)
        {
            // Stored URL: the hint shows scheme and host – enough to catch a retired host and a bearer token destined
            // for a host that is not Power Platform.
            if (TeamsUrlValidator.Validate(hint, teams.AuthMode, run.Context.IsDevelopment) is { Problem: TeamsUrlProblem.Retired } retired)
            {
                run.Error(path + ".url", ConfigValidationCodes.RetiredTeamsUrl, retired.Message ?? "Retired Teams webhook URL.");
            }
            else if (teams.AuthMode == TeamsAuthMode.EntraClientCredentials
                && SecretBindings.DeclaredUrlHost(channel) is { } host
                && !TeamsUrlValidator.IsPowerPlatformHost(host))
            {
                run.Error(path + ".url", ConfigValidationCodes.TeamsUrlFormat,
                    "With Entra client credentials the bearer token is sent only to *.environment.api.powerplatform.com.");
            }
        }

        if (!EntraCloudInfo.TeamsScopes(teams.Cloud).Contains(teams.Scope ?? string.Empty, StringComparer.Ordinal))
        {
            run.Error(path + ".teams.scope", ConfigValidationCodes.InvalidScope,
                "Only the documented Power Automate scopes of the selected cloud are accepted.", teams.Cloud.ToString());
        }

        if (teams.AuthMode == TeamsAuthMode.EntraClientCredentials && channel.Enabled)
        {
            OAuthIdentity.Validate(run, path + ".teams", teams.TenantId, teams.ClientId, teams.CertificateThumbprint,
                teams.ClientSecret, SecretNames.ChannelClientSecret(channel.Id));
        }
    }

    private static void Webhook(ValidationRun run, string path, ChannelSettings channel, WebhookChannelOptions webhook)
    {
        if (!string.IsNullOrEmpty(channel.Url.NewValue) && !UrlRules.IsAllowedHttpsUrl(channel.Url.NewValue, run.Context.IsDevelopment))
        {
            run.Error(path + ".url", ConfigValidationCodes.InsecureUrl, "Webhook URLs must be absolute https:// URLs.");
        }

        if (channel.Enabled && !SecretPresence.HasValue(webhook.SigningSecret, SecretNames.WebhookSigningSecret(channel.Id), run.Context))
        {
            run.Error(path + ".webhook.signingSecret", ConfigValidationCodes.Required, "Webhooks are always signed – a signing secret is required.");
        }

        SigningSecretFormat(run, path + ".webhook.signingSecret", webhook.SigningSecret);
        SigningSecretFormat(run, path + ".webhook.previousSigningSecret", webhook.PreviousSigningSecret);

        var allowed = webhook.AllowedHosts.Select(NormalizeHost).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (allowed.Count > 0 && SecretBindings.DeclaredUrlHost(channel) is { } host && !allowed.Contains(host))
        {
            run.Error(path + ".url", ConfigValidationCodes.HostNotAllowed, "The URL host is not in the allowed hosts of this webhook.");
        }

        if (webhook.HeartbeatInterval is { } heartbeat)
        {
            RangeCheck.Check(run, path + ".webhook.heartbeatInterval", heartbeat, TimeSpan.FromMinutes(5), TimeSpan.FromDays(1));
        }

        RangeCheck.Check(run, path + ".webhook.timeout", webhook.Timeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
    }

    private static void SigningSecretFormat(ValidationRun run, string path, SecretField? field)
    {
        if (!string.IsNullOrEmpty(field?.NewValue) && !StandardWebhookSigner.IsValidSecret(field.NewValue))
        {
            run.Error(path, ConfigValidationCodes.Required,
                $"A signing secret is \"{StandardWebhookSigner.SecretPrefix}\" followed by {StandardWebhookSigner.MinKeyBytes}–{StandardWebhookSigner.MaxKeyBytes} base64-encoded bytes.");
        }
    }

    // Same host form as the bindings (lower-case IDNA, no trailing dot) so "Siem.Contoso.Test." matches the URL host.
    private static string? NormalizeHost(string? host) =>
        string.IsNullOrWhiteSpace(host) ? null : SecretBindings.UrlHost("https://" + host.Trim() + "/");

    private static void Add(ValidationRun run, string path, string code, IssueSeverity severity, string message)
    {
        if (severity == IssueSeverity.Error)
        {
            run.Error(path, code, message);
        }
        else
        {
            run.Warning(path, code, message);
        }
    }
}
