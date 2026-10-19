using System.Globalization;
using System.Text;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Configuration;

/// <summary>
/// Canonical destination fingerprints ("bindings") of stored secrets (section 18). Every secret is stored with the
/// binding of the settings it was entered for; <c>DestinationGuard</c> (Service) compares it with the effective
/// settings before the secret is used, and the validator reports <c>secretRebindRequired</c> (8.3) when settings would
/// reuse a stored secret for another destination.
/// </summary>
/// <remarks>
/// Format: lower-case components separated by <c>|</c>; inside a component <c>%</c>, <c>|</c> and control characters
/// are percent-escaped, so two different settings never produce the same binding. GUIDs (tenant, client) are
/// normalized to the "D" form, host names (the SMTP host, URL hosts and explicit <c>urlHost</c> arguments) are in
/// lower-case IDNA/punycode form without a trailing dot.
/// A binding never contains a secret value: channel URLs contribute only their host.
/// <list type="table">
/// <item><term><c>smtp.password</c></term><description><c>smtp|&lt;host&gt;|&lt;port&gt;|&lt;userName&gt;|basic</c></description></item>
/// <item><term><c>smtp.oauth.clientSecret</c></term><description><c>smtp-oauth|&lt;cloud&gt;|&lt;tenantId&gt;|&lt;clientId&gt;|&lt;host&gt;</c></description></item>
/// <item><term><c>channel.{id}.url</c></term><description><c>channel|&lt;id&gt;|&lt;type&gt;</c></description></item>
/// <item><term><c>channel.{id}.clientSecret</c></term><description><c>teams-oauth|&lt;cloud&gt;|&lt;tenantId&gt;|&lt;clientId&gt;|&lt;scope&gt;|&lt;url host&gt;</c></description></item>
/// <item><term><c>channel.{id}.signingSecret</c>, <c>…previousSigningSecret</c></term><description><c>webhook|&lt;id&gt;|&lt;url host&gt;</c></description></item>
/// </list>
/// </remarks>
public static class SecretBindings
{
    /// <summary>Binding of <see cref="SecretNames.SmtpPassword"/>: <c>smtp|&lt;host&gt;|&lt;port&gt;|&lt;userName&gt;|basic</c>.</summary>
    public static string ForSmtpPassword(SmtpSettings smtp)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        return Join("smtp", Host(smtp.Host), smtp.Port.ToString(CultureInfo.InvariantCulture), Component(smtp.UserName), "basic");
    }

    /// <summary>Binding of <see cref="SecretNames.SmtpOAuthClientSecret"/>:
    /// <c>smtp-oauth|&lt;cloud&gt;|&lt;tenantId&gt;|&lt;clientId&gt;|&lt;host&gt;</c>. (That the host is one of the bootstrap
    /// <c>Notifications.OAuthSmtpHosts</c> is a separate check.)</summary>
    public static string ForSmtpOAuthClientSecret(SmtpSettings smtp)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        var oauth = smtp.OAuth ?? new SmtpOAuthSettings();
        return Join("smtp-oauth", Cloud(oauth.Cloud), Id(oauth.TenantId), Id(oauth.ClientId), Host(smtp.Host));
    }

    /// <summary>Binding of <see cref="SecretNames.ChannelUrl"/>: <c>channel|&lt;id&gt;|&lt;type&gt;</c> (the URL is its own
    /// destination; changing the channel type requires entering the URL again).</summary>
    public static string ForChannelUrl(ChannelSettings channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return Join("channel", Component(channel.Id), Component(channel.Type.ToString()));
    }

    /// <summary>Binding of <see cref="SecretNames.ChannelClientSecret"/>:
    /// <c>teams-oauth|&lt;cloud&gt;|&lt;tenantId&gt;|&lt;clientId&gt;|&lt;scope&gt;|&lt;url host&gt;</c>.</summary>
    /// <param name="channel">The Teams channel (its <see cref="ChannelSettings.Teams"/> options; defaults when null).</param>
    /// <param name="urlHost">Host of the channel URL (<see cref="UrlHost"/> of the new or the decrypted stored URL);
    /// null yields an empty host component, which never matches a stored binding (fail closed).</param>
    public static string ForTeamsClientSecret(ChannelSettings channel, string? urlHost)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var teams = channel.Teams ?? new TeamsChannelOptions();
        return Join("teams-oauth", Cloud(teams.Cloud), Id(teams.TenantId), Id(teams.ClientId), Component(teams.Scope), Host(urlHost));
    }

    /// <summary>Binding of <see cref="SecretNames.WebhookSigningSecret"/> and
    /// <see cref="SecretNames.WebhookPreviousSigningSecret"/>: <c>webhook|&lt;id&gt;|&lt;url host&gt;</c>.</summary>
    /// <param name="channel">The webhook channel.</param>
    /// <param name="urlHost">Host of the channel URL; null yields an empty host component (never matches).</param>
    public static string ForWebhookSigningSecret(ChannelSettings channel, string? urlHost)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return Join("webhook", Component(channel.Id), Host(urlHost));
    }

    /// <summary>The host of an absolute URL in binding form (lower-case IDNA, no trailing dot), or null when
    /// <paramref name="url"/> is not an absolute URL with a host. Also accepts a URL hint
    /// (<c>scheme://host[:port]/…</c>).</summary>
    public static string? UrlHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var host = Host(uri.IdnHost);
        return host.Length == 0 ? null : host;
    }

    /// <summary>The URL host a configuration document states for a channel: from <c>Url.NewValue</c> when the URL is
    /// being replaced, otherwise from <c>Url.Hint</c>. This is the validator's view (8.3); <c>DestinationGuard</c> and
    /// <c>ConfigSecretsApplier</c> use the host of the decrypted stored URL when no new value is supplied.</summary>
    public static string? DeclaredUrlHost(ChannelSettings channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var url = channel.Url ?? new SecretField();
        return !string.IsNullOrEmpty(url.NewValue) ? UrlHost(url.NewValue) : UrlHost(url.Hint);
    }

    /// <summary>
    /// Every secret field of <paramref name="configuration"/> with its canonical name, JSON path and expected binding:
    /// <c>smtp.password</c>, <c>smtp.oauth.clientSecret</c>, and per channel its URL, the Teams client secret (when
    /// <see cref="ChannelSettings.Teams"/> is set) and the two signing secrets (when <see cref="ChannelSettings.Webhook"/>
    /// is set). Channel paths use the list index (<c>channels[2].url</c>).
    /// </summary>
    /// <param name="configuration">The configuration (as received on PUT, or as stored).</param>
    /// <param name="urlHost">Resolves the URL host of a channel; null = <see cref="DeclaredUrlHost"/>.</param>
    public static IReadOnlyList<ConfiguredSecret> Enumerate(WatchdogConfiguration configuration, Func<ChannelSettings, string?>? urlHost = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        urlHost ??= DeclaredUrlHost;
        var smtp = configuration.Smtp ?? new SmtpSettings();
        var result = new List<ConfiguredSecret>
        {
            new()
            {
                Name = SecretNames.SmtpPassword,
                Path = "smtp.password",
                Field = smtp.Password ?? new SecretField(),
                Binding = ForSmtpPassword(smtp),
                InUse = smtp.Authentication == SmtpAuthentication.Basic,
            },
            new()
            {
                Name = SecretNames.SmtpOAuthClientSecret,
                Path = "smtp.oAuth.clientSecret",
                Field = smtp.OAuth?.ClientSecret ?? new SecretField(),
                Binding = ForSmtpOAuthClientSecret(smtp),
                InUse = smtp.Authentication == SmtpAuthentication.OAuth2ClientCredentials,
            },
        };

        var channels = configuration.Channels ?? [];
        for (var i = 0; i < channels.Count; i++)
        {
            var channel = channels[i];
            if (channel is null)
            {
                continue;
            }

            var path = string.Create(CultureInfo.InvariantCulture, $"channels[{i}]");
            result.Add(new ConfiguredSecret
            {
                Name = SecretNames.ChannelUrl(channel.Id),
                Path = path + ".url",
                Field = channel.Url ?? new SecretField(),
                Binding = ForChannelUrl(channel),
                ChannelId = channel.Id,
                InUse = true,
            });

            if (channel.Teams is { } teams)
            {
                result.Add(new ConfiguredSecret
                {
                    Name = SecretNames.ChannelClientSecret(channel.Id),
                    Path = path + ".teams.clientSecret",
                    Field = teams.ClientSecret ?? new SecretField(),
                    Binding = ForTeamsClientSecret(channel, urlHost(channel)),
                    ChannelId = channel.Id,
                    InUse = channel.Type == ChannelType.Teams && teams.AuthMode == TeamsAuthMode.EntraClientCredentials,
                });
            }

            if (channel.Webhook is { } webhook)
            {
                var binding = ForWebhookSigningSecret(channel, urlHost(channel));
                var inUse = channel.Type == ChannelType.Webhook;
                result.Add(new ConfiguredSecret
                {
                    Name = SecretNames.WebhookSigningSecret(channel.Id),
                    Path = path + ".webhook.signingSecret",
                    Field = webhook.SigningSecret ?? new SecretField(),
                    Binding = binding,
                    ChannelId = channel.Id,
                    InUse = inUse,
                });
                result.Add(new ConfiguredSecret
                {
                    Name = SecretNames.WebhookPreviousSigningSecret(channel.Id),
                    Path = path + ".webhook.previousSigningSecret",
                    Field = webhook.PreviousSigningSecret ?? new SecretField(),
                    Binding = binding,
                    ChannelId = channel.Id,
                    InUse = inUse,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// The <c>secretRebindRequired</c> rule (8.3, also for test sends): true when <paramref name="secret"/> is in use,
    /// a secret with that name is stored, the document supplies neither <c>NewValue</c> nor <c>Clear</c>, and the
    /// stored binding differs from <see cref="ConfiguredSecret.Binding"/>.
    /// </summary>
    /// <param name="secret">One entry of <see cref="Enumerate"/>.</param>
    /// <param name="storedBindings">Stored secret names → bindings (<c>ISecretStore.ListAsync</c>,
    /// <c>ConfigValidationContext.StoredSecrets</c>).</param>
    public static bool RequiresRebind(ConfiguredSecret secret, IReadOnlyDictionary<string, string> storedBindings)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(storedBindings);
        if (!secret.InUse || !string.IsNullOrEmpty(secret.Field.NewValue) || secret.Field.Clear)
        {
            return false;
        }

        return storedBindings.TryGetValue(secret.Name, out var stored)
            && !string.Equals(stored, secret.Binding, StringComparison.Ordinal);
    }

    private static string Join(params string[] components) => string.Join('|', components);

    private static string Cloud(EntraCloud cloud) => Component(cloud.ToString());

    private static string Id(string? value) =>
        Guid.TryParse(value, out var guid) ? guid.ToString("D", CultureInfo.InvariantCulture) : Component(value);

    // IDNA form for every host (SMTP host as typed, explicit URL hosts), so one destination has one binding: full-width
    // letters, upper case, U+3002 dots and a trailing dot all collapse. A non-ASCII host that is not valid IDNA is kept
    // as written (escaped); it contains non-ASCII characters and can therefore never equal an IDNA binding.
    private static string Host(string? host)
    {
        var value = (host ?? string.Empty).Trim();
        if (!Ascii.IsValid(value))
        {
            try
            {
                value = new IdnMapping().GetAscii(value);
            }
            catch (ArgumentException)
            {
                // Not a valid IDN host: keep it (fail closed – it never matches a binding of a real host).
            }
        }

        return Component(value.TrimEnd('.'));
    }

    private static string Component(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var lower = value.Trim().ToLowerInvariant();
        if (!lower.AsSpan().ContainsAny("%|") && !lower.Any(char.IsControl))
        {
            return lower;
        }

        var builder = new StringBuilder(lower.Length + 8);
        foreach (var c in lower)
        {
            if (c is '%' or '|' || char.IsControl(c))
            {
                builder.Append('%').Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
