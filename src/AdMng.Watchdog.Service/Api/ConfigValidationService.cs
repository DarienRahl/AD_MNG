using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// Validation of a configuration submitted to the API (spec 17.2 step 2): the Core validator with a
/// <see cref="ConfigValidationContext"/> built from the stored secret bindings, the environment and the bootstrap flags;
/// plus the service's own <c>secretRebindRequired</c> check with URL hosts from the real stored URLs
/// (<see cref="ConfigSecretsApplier"/>), – for newly added <c>additionalWatchedAttributes</c> – the live session
/// schema (exists, not confidential, not constructed, not a back link; <c>schemaUnavailable</c> without a session), and
/// – for newly added <c>excludedSubtrees</c> without <c>Authorization.AllowTier0Suppression</c> – <c>tier0Suppression</c>
/// when the subtree holds a privileged object of the snapshot (<see cref="PrivilegedSubtreeIssuesAsync"/>).
/// </summary>
public sealed class ConfigValidationService(
    IConfigurationValidator validator,
    ISecretStore secrets,
    ConfigSecretsApplier applier,
    IDirectorySessionProvider directory,
    IDetectionStore snapshot,
    IConfigTokenSource tokens,
    IHostEnvironment environment,
    IOptionsMonitor<ServiceOptions> options,
    DetectionHostOptions? hostOptions = null)
{
    /// <summary>The well-known accounts protected besides the privileged groups and their members: the built-in
    /// Administrator (RID 500) and krbtgt (RID 502).</summary>
    private static readonly string[] ProtectedAccountSids = [ConfigTokens.DomainSidToken + "-500", ConfigTokens.DomainSidToken + "-502"];

    /// <summary>The attribution sources of this host (stage B design 7.8): the Linux service reads OpenWEC files only; the
    /// Windows service (until 0.3.0) keeps every source.</summary>
    public static IReadOnlyList<AttributionSourceKind> SupportedAttributionSources { get; } =
        OperatingSystem.IsLinux() ? [AttributionSourceKind.OpenWec, AttributionSourceKind.None] : [];

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
            MinEventsDays = settings.Retention.MinEventsDays,
            SupportedAttributionSources = SupportedAttributionSources,
        };

        var issues = validator.Validate(configuration, context).Issues.ToList();
        Merge(issues, ConfigSecretsApplier.RebindIssues(configuration, stored, hosts));
        if (previous is not null)
        {
            Merge(issues, await SchemaIssuesAsync(configuration, previous, cancellationToken).ConfigureAwait(false));
            if (!settings.Authorization.AllowTier0Suppression)
            {
                Merge(issues, await PrivilegedSubtreeIssuesAsync(configuration, previous, cancellationToken).ConfigureAwait(false));
            }
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

    /// <summary>
    /// <c>tier0Suppression</c> against the live directory (8.3, 12.6): the static catalog protects <c>CN=Users</c>,
    /// <c>CN=Builtin</c> and the configured DNs, but tiered environments move privileged groups and admin accounts into
    /// their own OUs. A newly added <c>excludedSubtrees</c> entry that equals or contains a privileged object of the
    /// snapshot – a privileged root group (critical/high SIDs, the watchdog's own role and host groups, the additional
    /// DNs), a group on a path to one, a privileged principal of the current privilege state, krbtgt or the built-in
    /// Administrator – would silence the detection of exactly those objects, so it needs the bootstrap flag as well. Only
    /// entries added by this save are checked: an existing exclusion never blocks an unrelated save.
    /// </summary>
    private async Task<IReadOnlyList<ConfigIssue>> PrivilegedSubtreeIssuesAsync(WatchdogConfiguration configuration, WatchdogConfiguration previous,
        CancellationToken cancellationToken)
    {
        var before = (previous.Directory?.ExcludedSubtrees ?? []).Where(v => v is not null).Select(v => v.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded = configuration.Directory?.ExcludedSubtrees ?? [];
        var added = Enumerable.Range(0, excluded.Count).Where(i => !string.IsNullOrWhiteSpace(excluded[i]) && !before.Contains(excluded[i].Trim())).ToList();
        if (added.Count == 0)
        {
            return [];
        }

        var privileged = await PrivilegedDnsAsync(configuration, cancellationToken).ConfigureAwait(false);
        return [.. added
            .Where(i => privileged.Any(dn => Covers(excluded[i], dn)))
            .Select(i => new ConfigIssue
            {
                Path = $"directory.excludedSubtrees[{i}]",
                Code = ConfigValidationCodes.Tier0Suppression,
                Severity = IssueSeverity.Error,
                Message = "The subtree contains a privileged group, a privileged account, krbtgt or the built-in Administrator; excluding it needs Authorization.AllowTier0Suppression.",
            })];
    }

    /// <summary>The DNs of the privileged objects in the snapshot (see <see cref="PrivilegedSubtreeIssuesAsync"/>).</summary>
    private async Task<List<DistinguishedName>> PrivilegedDnsAsync(WatchdogConfiguration configuration, CancellationToken cancellationToken)
    {
        var values = tokens.Current;
        var groups = configuration.PrivilegedGroups ?? new PrivilegedGroupSettings();
        var dns = new List<DistinguishedName>();
        void Add(string? dn)
        {
            if (DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot)
            {
                dns.Add(parsed);
            }
        }

        foreach (var sid in groups.CriticalSids.Concat(groups.HighSids).Concat(hostOptions?.RoleGroupSids ?? []).Concat(ProtectedAccountSids))
        {
            if (values.ExpandSid(sid) is { Sid: { } exact }
                && await snapshot.FindBySidAsync(exact, cancellationToken).ConfigureAwait(false) is { IsDeleted: false } found)
            {
                Add(found.Dn);
            }
        }

        if (await snapshot.GetPrivilegeStateAsync(cancellationToken).ConfigureAwait(false) is { } state)
        {
            var guids = state.Principals
                .SelectMany(p => p.Path.Append(p.GroupGuid).Append(Guid.TryParseExact(p.PrincipalKey, "N", out var key) ? key : Guid.Empty))
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToList();
            foreach (var found in (await snapshot.GetSnapshotsAsync(guids, cancellationToken).ConfigureAwait(false)).Values.Where(o => !o.IsDeleted))
            {
                Add(found.Dn);
            }

            foreach (var principal in state.Principals.Where(p => p.PrincipalKey.StartsWith("dn:", StringComparison.OrdinalIgnoreCase)))
            {
                Add(principal.PrincipalKey[3..]);
            }
        }

        return dns;
    }

    /// <summary>True when the configured excluded subtree equals or contains <paramref name="privileged"/>; a
    /// <c>{domain}</c> token expands to the domain components of the snapshot DN (one watchdog watches one domain).</summary>
    private static bool Covers(string excludedValue, DistinguishedName privileged)
    {
        var domainTokens = ConfigTokens.Unknown with { DomainDn = privileged.DomainComponent.ToString() };
        return domainTokens.ExpandDn(excludedValue) is { } expanded
            && DistinguishedName.TryParse(expanded, out var subtree)
            && !subtree.IsRoot
            && (privileged.Equals(subtree) || privileged.IsDescendantOf(subtree));
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
