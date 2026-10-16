using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Secrets;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// Secret fields of a PUT configuration → <see cref="SecretChange"/>s (spec 17.2 step 3, section 18): <c>Clear</c>
/// removes, <c>NewValue</c> stores with the binding of the NEW settings (<see cref="SecretBindings"/>) and a hint (URLs
/// <c>scheme://host/first-segment/…</c>, signing secrets <c>whsec_…</c> + last 4 characters, passwords none); secrets that
/// no field references any more (removed channels, removed Teams/webhook options) are removed; every field is stripped
/// to <c>IsSet</c>/<c>Hint</c>. URL hosts for the Teams/webhook bindings come from the new URL or the decrypted stored
/// one – never from the client-supplied hint.
/// </summary>
public sealed class ConfigSecretsApplier(ISecretStore secrets)
{
    /// <summary>The URL host of every channel: from <c>NewValue</c>, else from the stored (decrypted) URL secret.</summary>
    public async Task<IReadOnlyDictionary<string, string?>> ResolveUrlHostsAsync(WatchdogConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var hosts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in configuration.Channels ?? [])
        {
            if (channel is null || hosts.ContainsKey(channel.Id))
            {
                continue;
            }

            if (channel.Url is { NewValue: { Length: > 0 } newUrl })
            {
                hosts[channel.Id] = SecretBindings.UrlHost(newUrl);
                continue;
            }

            try
            {
                var stored = await secrets.GetAsync(SecretNames.ChannelUrl(channel.Id), cancellationToken).ConfigureAwait(false);
                hosts[channel.Id] = stored is null ? null : SecretBindings.UrlHost(stored.Value.Reveal());
            }
            catch (SecretUnreadableException)
            {
                hosts[channel.Id] = null;
            }
        }

        return hosts;
    }

    /// <summary>The <c>secretRebindRequired</c> issues of <paramref name="configuration"/> against the stored bindings.</summary>
    public static IReadOnlyList<ConfigIssue> RebindIssues(
        WatchdogConfiguration configuration,
        IReadOnlyDictionary<string, string> storedBindings,
        IReadOnlyDictionary<string, string?> urlHosts)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(storedBindings);
        ArgumentNullException.ThrowIfNull(urlHosts);
        return [.. SecretBindings.Enumerate(configuration, c => urlHosts.GetValueOrDefault(c.Id))
            .Where(s => SecretBindings.RequiresRebind(s, storedBindings))
            .Select(s => new ConfigIssue
            {
                Path = s.Path,
                Code = "secretRebindRequired",
                Severity = IssueSeverity.Error,
                Message = "The destination of a stored secret changed; enter the secret again.",
            })];
    }

    /// <summary>Builds the secret changes and the stripped configuration.</summary>
    public static ConfigSecretsResult Apply(
        WatchdogConfiguration incoming,
        WatchdogConfiguration previous,
        IReadOnlyDictionary<string, string> storedBindings,
        IReadOnlyDictionary<string, string?> urlHosts)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(storedBindings);
        ArgumentNullException.ThrowIfNull(urlHosts);
        var previousHints = SecretBindings.Enumerate(previous)
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Field.Hint, StringComparer.OrdinalIgnoreCase);
        var changes = new List<SecretChange>();
        var fields = new Dictionary<string, SecretField>(StringComparer.OrdinalIgnoreCase);
        foreach (var secret in SecretBindings.Enumerate(incoming, c => urlHosts.GetValueOrDefault(c.Id)))
        {
            var stored = storedBindings.ContainsKey(secret.Name);
            if (secret.Field.Clear)
            {
                if (stored)
                {
                    changes.Add(new SecretChange(secret.Name, null, null));
                }

                fields[secret.Name] = new SecretField();
            }
            else if (secret.Field.NewValue is { Length: > 0 } value)
            {
                changes.Add(new SecretChange(secret.Name, new SecretValue(value), secret.Binding));
                fields[secret.Name] = new SecretField { IsSet = true, Hint = Hint(secret.Name, value) };
            }
            else
            {
                fields[secret.Name] = new SecretField { IsSet = stored, Hint = stored ? previousHints.GetValueOrDefault(secret.Name) : null };
            }
        }

        foreach (var orphan in storedBindings.Keys.Where(name => !fields.ContainsKey(name)))
        {
            changes.Add(new SecretChange(orphan, null, null));
        }

        return new ConfigSecretsResult(Strip(incoming, fields), changes);
    }

    /// <summary>The hint shown for a newly entered secret (never the secret itself).</summary>
    public static string? Hint(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (name.EndsWith(".url", StringComparison.Ordinal))
        {
            return UrlHint(value);
        }

        if (name.EndsWith("igningSecret", StringComparison.Ordinal))
        {
            return value.Length >= 12 ? "whsec_…" + value[^4..] : "whsec_…";
        }

        return null;
    }

    private static string? UrlHint(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var first = segments.Length > 0 ? "/" + segments[0] + (segments.Length > 1 ? "/…" : string.Empty) : string.Empty;
        return $"{uri.Scheme}://{uri.IdnHost}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port)}{first}";
    }

    private static WatchdogConfiguration Strip(WatchdogConfiguration configuration, Dictionary<string, SecretField> fields)
    {
        SecretField Field(string name) => fields.TryGetValue(name, out var field) ? field : new SecretField();

        var smtp = configuration.Smtp ?? new SmtpSettings();
        var oauth = smtp.OAuth ?? new SmtpOAuthSettings();
        return configuration with
        {
            Smtp = smtp with
            {
                Password = Field(SecretNames.SmtpPassword),
                OAuth = oauth with { ClientSecret = Field(SecretNames.SmtpOAuthClientSecret) },
            },
            Channels = [.. (configuration.Channels ?? []).Where(c => c is not null).Select(channel => channel with
            {
                Url = Field(SecretNames.ChannelUrl(channel.Id)),
                Teams = channel.Teams is { } teams ? teams with { ClientSecret = Field(SecretNames.ChannelClientSecret(channel.Id)) } : null,
                Webhook = channel.Webhook is { } webhook
                    ? webhook with
                    {
                        SigningSecret = Field(SecretNames.WebhookSigningSecret(channel.Id)),
                        PreviousSigningSecret = Field(SecretNames.WebhookPreviousSigningSecret(channel.Id)),
                    }
                    : null,
            })],
        };
    }
}
