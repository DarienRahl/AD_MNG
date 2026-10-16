using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Configuration;

/// <summary>
/// The Viewer view of the runtime configuration (<c>GET /api/v1/config</c> for non-Admins, section 17.2). The result has
/// <see cref="ConfigEnvelope.IsRedacted"/> = true and is never accepted by PUT (8.2 <c>redactedConfig</c>).
/// </summary>
/// <remarks>
/// Removed: every rule exclusion (each one is replaced by a placeholder, so <c>rule.Exclusions.Count</c> stays the
/// count – see <see cref="RedactedExclusionIdPrefix"/>); every rule actor exclusion
/// (<c>ActorFilter.ExcludeActors</c>: each SID replaced by a placeholder <c>redacted-&lt;n&gt;</c>, so only the count
/// remains – like exclusions they reveal monitoring blind spots); recipient group addresses; webhook
/// <c>AllowedHosts</c> (like recipients they reveal where alerts go); SMTP host, user name, from, reply-to and local
/// domain; the OAuth tenant id, client id and certificate thumbprint (SMTP and Teams); every secret hint (channel URL
/// hints, signing-secret hints, …). Secret fields keep only <see cref="SecretField.IsSet"/>. Everything else is
/// returned unchanged; the input is not modified.
/// </remarks>
public sealed class ConfigRedactor
{
    /// <summary>Id prefix of the placeholder exclusions: <c>redacted-1</c>, <c>redacted-2</c>, … with an empty reason and
    /// creator, <c>CreatedAt</c> = <c>default</c> and no object, subtree, actor, attribute or expiry. The placeholders of
    /// <c>ActorFilter.ExcludeActors</c> use the same form (never a valid SID, so a redacted document fails validation
    /// even without the <c>IsRedacted</c> flag).</summary>
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
            ? webhook with
            {
                SigningSecret = Hide(webhook.SigningSecret),
                PreviousSigningSecret = Hide(webhook.PreviousSigningSecret),
                AllowedHosts = [],
            }
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
                Id = Placeholder(i),
                Reason = string.Empty,
                CreatedBy = string.Empty,
                CreatedAt = default,
            };
        }

        var actors = rule.Actors is { } filter
            ? filter with { ExcludeActors = [.. Enumerable.Range(0, filter.ExcludeActors?.Count ?? 0).Select(Placeholder)] }
            : rule.Actors;
        return rule with { Exclusions = placeholders, Actors = actors! };
    }

    private static string Placeholder(int index) =>
        RedactedExclusionIdPrefix + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static SecretField Hide(SecretField? field) => new() { IsSet = field?.IsSet ?? false };
}
