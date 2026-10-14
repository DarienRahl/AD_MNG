using System.Globalization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;

namespace AdMng.Watchdog.Service.Hosting.Fallbacks;

/// <summary>
/// <see cref="IConfigurationValidator"/> used only when the Core registration provides none. It checks the
/// security-relevant subset of section 8.3 – <c>secretRebindRequired</c>, <c>oauthHostNotAllowed</c>, unique channel
/// ids and the schema version – so a service without the full validator never accepts a configuration that would send
/// a stored credential to a new destination.
/// </summary>
public sealed class FallbackConfigurationValidator : IConfigurationValidator
{
    public ConfigValidationResult Validate(WatchdogConfiguration configuration, ConfigValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);
        var issues = new List<ConfigIssue>();
        if (configuration.SchemaVersion != WatchdogConfiguration.CurrentSchemaVersion)
        {
            issues.Add(Error("schemaVersion", "schemaVersion", "Unsupported configuration schema version."));
        }

        foreach (var secret in SecretBindings.Enumerate(configuration))
        {
            if (SecretBindings.RequiresRebind(secret, context.StoredSecrets))
            {
                issues.Add(Error(secret.Path, "secretRebindRequired", "The destination of a stored secret changed; enter the secret again."));
            }
        }

        var smtp = configuration.Smtp ?? new SmtpSettings();
        if (smtp.Authentication == SmtpAuthentication.OAuth2ClientCredentials
            && !context.OAuthSmtpHosts.Any(h => string.Equals(h.Trim().TrimEnd('.'), (smtp.Host ?? string.Empty).Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(Error("smtp.host", "oauthHostNotAllowed", "XOAUTH2 tokens may only be sent to the hosts in Notifications.OAuthSmtpHosts."));
        }

        var channels = configuration.Channels ?? [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < channels.Count; i++)
        {
            if (channels[i] is { } channel && !seen.Add(channel.Id))
            {
                issues.Add(Error(string.Create(CultureInfo.InvariantCulture, $"channels[{i}].id"), "duplicateId", "Channel ids must be unique."));
            }
        }

        return new ConfigValidationResult { Issues = issues };
    }

    private static ConfigIssue Error(string path, string code, string message) =>
        new() { Path = path, Code = code, Severity = IssueSeverity.Error, Message = message };
}
