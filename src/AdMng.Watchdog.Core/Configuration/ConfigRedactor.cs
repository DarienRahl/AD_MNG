using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Configuration;

/// <summary>
/// The Viewer view of the runtime configuration (<c>GET /api/v1/config</c> for non-Admins, section 17.2). The result has
/// <see cref="ConfigEnvelope.IsRedacted"/> = true and is never accepted by PUT (8.2 <c>redactedConfig</c>).
/// </summary>
/// <remarks>
/// Removed: every rule exclusion (each one is replaced by a placeholder, so <c>rule.Exclusions.Count</c> stays the
/// count – see <see cref="RedactedExclusionIdPrefix"/>); recipient group addresses; SMTP host, user name, from,
/// reply-to and local domain; the OAuth tenant id, client id and certificate thumbprint (SMTP and Teams); every secret
/// hint (channel URL hints, signing-secret hints, …). Secret fields keep only <see cref="SecretField.IsSet"/>.
/// Everything else is returned unchanged; the input is not modified.
/// </remarks>
public sealed class ConfigRedactor
{
    /// <summary>Id prefix of the placeholder exclusions: <c>redacted-1</c>, <c>redacted-2</c>, … with an empty reason and
    /// creator, <c>CreatedAt</c> = <c>default</c> and no object, subtree, actor, attribute or expiry.</summary>
    public const string RedactedExclusionIdPrefix = "redacted-";

    /// <summary>The redacted copy of <paramref name="envelope"/> (version and update metadata kept, IsRedacted = true).</summary>
    public ConfigEnvelope Redact(ConfigEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope with { Configuration = Redact(envelope.Configuration), IsRedacted = true };
    }

    /// <summary>The redacted copy of <paramref name="configuration"/>.</summary>
    public WatchdogConfiguration Redact(WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var smtp = configuration.Smtp ?? new SmtpSettings();
        var oauth = smtp.OAuth ?? new SmtpOAuthSettings();
        return configuration with
        {
            Smtp = smtp with
            {
                Host = null,
                UserName = null,
                From = null,
                ReplyTo = null,
                LocalDomain = null,
                Password = Hide(smtp.Password),
                OAuth = oauth with
                {
                    TenantId = null,
                    ClientId = null,
                    CertificateThumbprint = null,
                    ClientSecret = Hide(oauth.ClientSecret),
                },
            },
            // Null list entries (never valid, but possible in a stored document) are kept as null rather than failing GET.
            Channels = [.. (configuration.Channels ?? []).Select(c => c is null ? c! : RedactChannel(c))],
            RecipientGroups = [.. (configuration.RecipientGroups ?? []).Select(g => g is null ? g! : g with { Addresses = [] })],
            Rules = [.. (configuration.Rules ?? []).Select(r => r is null ? r! : RedactRule(r))],
        };
    }

    private static ChannelSettings RedactChannel(ChannelSettings channel) => channel with
    {
        Url = Hide(channel.Url),
        Teams = channel.Teams is { } teams
            ? teams with { TenantId = null, ClientId = null, CertificateThumbprint = null, ClientSecret = Hide(teams.ClientSecret) }
            : null,
        Webhook = channel.Webhook is { } webhook
            ? webhook with { SigningSecret = Hide(webhook.SigningSecret), PreviousSigningSecret = Hide(webhook.PreviousSigningSecret) }
            : null,
    };

    private static RuleSettings RedactRule(RuleSettings rule)
    {
        var count = rule.Exclusions?.Count ?? 0;
        var placeholders = new RuleExclusion[count];
        for (var i = 0; i < count; i++)
        {
            placeholders[i] = new RuleExclusion
            {
                Id = RedactedExclusionIdPrefix + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Reason = string.Empty,
                CreatedBy = string.Empty,
                CreatedAt = default,
            };
        }

        return rule with { Exclusions = placeholders };
    }

    private static SecretField Hide(SecretField? field) => new() { IsSet = field?.IsSet ?? false };
}
