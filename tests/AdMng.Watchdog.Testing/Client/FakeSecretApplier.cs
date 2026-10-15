using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Testing.Client;

/// <summary>
/// What <c>PUT /api/v1/config</c> does with the write-only secret fields (17.2 step 3): <see cref="SecretField.NewValue"/>
/// becomes <c>IsSet</c> with a hint (URLs: scheme://host/first-segment/…, webhook secrets: whsec_… + the last four
/// characters, passwords: none), <see cref="SecretField.Clear"/> removes the secret; neither is ever returned.
/// </summary>
public static class FakeSecretApplier
{
    /// <summary>The configuration as the service would return it after saving.</summary>
    public static WatchdogConfiguration Apply(WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var smtp = configuration.Smtp;
        return configuration with
        {
            Smtp = smtp with
            {
                Password = Applied(smtp.Password, HintKind.None),
                OAuth = smtp.OAuth with { ClientSecret = Applied(smtp.OAuth.ClientSecret, HintKind.None) },
            },
            Channels = [.. configuration.Channels.Select(ApplyChannel)],
        };
    }

    /// <summary>True when any secret field of the configuration carries a new value or a clear request.</summary>
    public static bool HasPendingSecrets(WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IEnumerable<SecretField> fields =
        [
            configuration.Smtp.Password,
            configuration.Smtp.OAuth.ClientSecret,
            .. configuration.Channels.SelectMany(c => new[]
            {
                c.Url,
                c.Teams?.ClientSecret ?? new SecretField(),
                c.Webhook?.SigningSecret ?? new SecretField(),
                c.Webhook?.PreviousSigningSecret ?? new SecretField(),
            }),
        ];
        return fields.Any(f => f.NewValue is not null || f.Clear);
    }

    /// <summary>"https://host/first-segment/…" for a URL (never the full URL, never query parameters).</summary>
    public static string? UrlHint(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var firstSegment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstSegment is null ? $"{uri.Scheme}://{uri.Host}/…" : $"{uri.Scheme}://{uri.Host}/{firstSegment}/…";
    }

    /// <summary>The host of a hint or URL ("siem.contoso.com"), or null.</summary>
    public static string? HostOf(string? urlOrHint)
    {
        if (string.IsNullOrEmpty(urlOrHint))
        {
            return null;
        }

        var text = urlOrHint.Replace("…", string.Empty, StringComparison.Ordinal);
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private static ChannelSettings ApplyChannel(ChannelSettings channel) => channel with
    {
        Url = Applied(channel.Url, HintKind.Url),
        Teams = channel.Teams is { } teams ? teams with { ClientSecret = Applied(teams.ClientSecret, HintKind.None) } : null,
        Webhook = channel.Webhook is { } webhook
            ? webhook with
            {
                SigningSecret = Applied(webhook.SigningSecret, HintKind.SigningSecret),
                PreviousSigningSecret = Applied(webhook.PreviousSigningSecret, HintKind.SigningSecret),
            }
            : null,
    };

    private static SecretField Applied(SecretField field, HintKind hint)
    {
        if (field.Clear)
        {
            return new SecretField();
        }

        if (field.NewValue is not { } value)
        {
            return field with { NewValue = null, Clear = false };
        }

        return new SecretField
        {
            IsSet = true,
            Hint = hint switch
            {
                HintKind.Url => UrlHint(value),
                HintKind.SigningSecret => value.Length > 10 ? $"whsec_…{value[^4..]}" : "whsec_…",
                _ => null,
            },
        };
    }

    private enum HintKind
    {
        None,
        Url,
        SigningSecret,
    }
}
