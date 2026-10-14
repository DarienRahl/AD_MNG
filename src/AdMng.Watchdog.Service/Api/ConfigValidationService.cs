using AdMng.Core.Directory;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// Validation of a configuration submitted to the API (spec 17.2 step 2): the Core validator with a
/// <see cref="ConfigValidationContext"/> built from the stored secret bindings, the environment and the bootstrap flags;
/// plus the service's own <c>secretRebindRequired</c> check with URL hosts from the real stored URLs
/// (<see cref="ConfigSecretsApplier"/>), and – for newly added <c>additionalWatchedAttributes</c> – the live session
/// schema (exists, not confidential, not constructed, not a back link; <c>schemaUnavailable</c> without a session).
/// </summary>
public sealed class ConfigValidationService(
    IConfigurationValidator validator,
    ISecretStore secrets,
    ConfigSecretsApplier applier,
    IDirectorySessionProvider directory,
    IHostEnvironment environment,
    IOptionsMonitor<ServiceOptions> options)
{
    /// <summary>Validates <paramref name="configuration"/> (with <paramref name="previous"/> for the schema check).</summary>
    public async Task<ValidationOutcome> ValidateAsync(WatchdogConfiguration configuration, WatchdogConfiguration? previous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var stored = await secrets.ListAsync(cancellationToken).ConfigureAwait(false);
        var hosts = await applier.ResolveUrlHostsAsync(configuration, cancellationToken).ConfigureAwait(false);
        var settings = options.CurrentValue;
        var context = new ConfigValidationContext
        {
            StoredSecrets = stored,
            IsDevelopment = environment.IsDevelopment(),
            AllowTier0Suppression = settings.Authorization.AllowTier0Suppression,
            OAuthSmtpHosts = settings.Notifications.EffectiveOAuthSmtpHosts,
        };

        var issues = validator.Validate(configuration, context).Issues.ToList();
        Merge(issues, ConfigSecretsApplier.RebindIssues(configuration, stored, hosts));
        if (previous is not null)
        {
            Merge(issues, await SchemaIssuesAsync(configuration, previous, cancellationToken).ConfigureAwait(false));
        }

        return new ValidationOutcome(new ConfigValidationResult { Issues = issues }, stored, hosts);
    }

    private async Task<IReadOnlyList<ConfigIssue>> SchemaIssuesAsync(WatchdogConfiguration configuration, WatchdogConfiguration previous, CancellationToken cancellationToken)
    {
        var before = (previous.Directory?.AdditionalWatchedAttributes ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = (configuration.Directory?.AdditionalWatchedAttributes ?? []).Where(a => !before.Contains(a)).ToList();
        if (added.Count == 0)
        {
            return [];
        }

        IDirectorySession session;
        try
        {
            session = await directory.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException)
        {
            return [Issue("directory.additionalWatchedAttributes", "schemaUnavailable", "New watched attributes can be checked only while the directory is connected.")];
        }

        var issues = new List<ConfigIssue>();
        var all = configuration.Directory!.AdditionalWatchedAttributes;
        for (var i = 0; i < all.Count; i++)
        {
            if (before.Contains(all[i]))
            {
                continue;
            }

            var definition = session.Schema.FindAttribute(all[i]);
            if (definition is null || definition.IsConfidential || definition.IsConstructed || definition.LinkId is { } link && link % 2 == 1)
            {
                issues.Add(Issue($"directory.additionalWatchedAttributes[{i}]", "invalidAttribute",
                    "The attribute does not exist in the schema, is confidential, constructed or a back link."));
            }
        }

        return issues;
    }

    private static void Merge(List<ConfigIssue> issues, IEnumerable<ConfigIssue> extra)
    {
        foreach (var issue in extra)
        {
            if (!issues.Any(i => string.Equals(i.Path, issue.Path, StringComparison.Ordinal) && string.Equals(i.Code, issue.Code, StringComparison.Ordinal)))
            {
                issues.Add(issue);
            }
        }
    }

    private static ConfigIssue Issue(string path, string code, string message) =>
        new() { Path = path, Code = code, Severity = IssueSeverity.Error, Message = message };
}
