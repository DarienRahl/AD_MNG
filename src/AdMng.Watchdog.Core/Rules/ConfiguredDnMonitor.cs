using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// Health check <see cref="CheckCode"/> (Warning): DN-based settings stop working silently when the OU or group they
/// name is renamed or moved – a rule scoped to <c>OU=Warszawa</c> matches nothing after the OU became
/// <c>OU=Warszawa-Centrum</c>, a group in <c>privilegedGroups.additionalCriticalDns</c> stops being a privileged root.
/// Every configured DN (rule scopes, rule group DNs and exclusion subtrees, the additional critical/high and watch-list
/// groups, critical objects, service-account and disabled-account subtrees, wake-up containers) is resolved against the
/// snapshot; the ones that no longer exist are listed (Args, as configured). DNs outside the default naming context, under
/// <c>CN=System</c> or under an excluded subtree, and <c>CN=Builtin</c> itself (a builtinDomain object, not a container),
/// are not snapshotted and therefore not checked; nothing is reported
/// before the baseline. The service never rewrites the configuration itself. Runs at most every <see cref="Interval"/>
/// per configuration (called by <see cref="RuleEvaluationJob"/>).
/// </summary>
public sealed class ConfiguredDnMonitor(IDetectionStore snapshot, IHealthRegistry health, TimeProvider timeProvider)
{
    /// <summary>The health check code.</summary>
    public const string CheckCode = "rules.configuredDns";

    /// <summary>At most this many missing DNs are listed.</summary>
    public const int MaxListed = 20;

    /// <summary>Minimum distance between two checks of one configuration (renames are picked up within it).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private (WatchdogConfiguration Configuration, ConfigTokens Tokens, DateTimeOffset At)? _last;

    /// <summary>Checks <paramref name="configuration"/> unless it was checked less than <see cref="Interval"/> ago.</summary>
    public async Task CheckAsync(WatchdogConfiguration configuration, ConfigTokens tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tokens);
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_last is { } last && ReferenceEquals(last.Configuration, configuration) && last.Tokens == tokens && now - last.At < Interval)
            {
                return;
            }
        }

        if (!DistinguishedName.TryParse(tokens.DomainDn, out var domain) || domain.IsRoot
            || await snapshot.FindByDnAsync(domain.ToString(), cancellationToken).ConfigureAwait(false) is null)
        {
            return;   // tokens unknown or no baseline yet: everything would look missing
        }

        var excluded = (configuration.Directory?.ExcludedSubtrees ?? []).Select(e => Parse(e, tokens)).OfType<DistinguishedName>().ToList();
        var system = domain.Child("CN", "System");
        var builtin = domain.Child("CN", "Builtin");   // a builtinDomain object: its groups are snapshotted, it is not
        var missing = new List<string>();
        foreach (var configured in Configured(configuration).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Parse(configured, tokens) is not { } dn
                || !(dn.Equals(domain) || dn.IsDescendantOf(domain))
                || dn.Equals(system) || dn.IsDescendantOf(system) || dn.Equals(builtin)
                || excluded.Any(e => dn.Equals(e) || dn.IsDescendantOf(e)))
            {
                continue;
            }

            if (await snapshot.FindByDnAsync(dn.ToString(), cancellationToken).ConfigureAwait(false) is not { IsDeleted: false })
            {
                missing.Add(configured);
            }
        }

        health.Report(new HealthCheckDto
        {
            Code = CheckCode,
            State = missing.Count > 0 ? HealthState.Warning : HealthState.Ok,
            Detail = missing.Count > 0
                ? "Configured DNs no longer exist (renamed or moved?) – rules, privileged groups or subtrees that use them match nothing."
                : null,
            Args = [.. missing.Take(MaxListed)],
            CheckedAt = now,
        });
        lock (_gate)
        {
            _last = (configuration, tokens, now);
        }
    }

    private static IEnumerable<string> Configured(WatchdogConfiguration configuration)
    {
        var directory = configuration.Directory ?? new DirectorySettings();
        var groups = configuration.PrivilegedGroups ?? new PrivilegedGroupSettings();
        IEnumerable<string?> all =
        [
            .. directory.CriticalObjects ?? [], .. directory.ServiceAccountSubtrees ?? [], .. directory.DisabledAccountsSubtrees ?? [],
            .. directory.WakeUpContainers ?? [], .. groups.AdditionalCriticalDns ?? [], .. groups.AdditionalHighDns ?? [],
            .. groups.WatchListDns ?? [],
        ];
        foreach (var rule in (configuration.Rules ?? []).Where(r => r is { Enabled: true }))
        {
            all = all
                .Concat(rule.Scope?.IncludeSubtrees ?? [])
                .Concat(rule.Scope?.ExcludeSubtrees ?? [])
                .Concat(rule.GroupDns ?? [])
                .Concat((rule.Exclusions ?? []).Select(e => e?.SubtreeDn));
        }

        return all.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim());
    }

    private static DistinguishedName? Parse(string? configured, ConfigTokens tokens) =>
        tokens.ExpandDn(configured) is { } expanded && DistinguishedName.TryParse(expanded, out var dn) && !dn.IsRoot ? dn : null;
}
