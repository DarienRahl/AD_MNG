using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Health check <c>attribution.actorConditionsUnreliable</c> (section 11.7): Warning when an enabled rule uses actor
/// conditions (<c>ExcludeActors</c>, <c>ExcludeSelfChanges</c>, actor exclusions) for kinds whose attribution ended
/// SACL_MISSING or AUDIT_POLICY_MISSING in the last 24 hours – such conditions cannot work (an unresolved actor
/// excludes nothing). Args: pairs of rule id and kind.
/// </summary>
public sealed class ActorConditionHealthMonitor(TimeProvider timeProvider)
{
    /// <summary>The health check code.</summary>
    public const string CheckCode = "attribution.actorConditionsUnreliable";

    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly Lock _gate = new();
    private readonly Dictionary<WatchEventKind, DateTimeOffset> _uncoverable = [];

    /// <summary>Records the final reason of an attribution.</summary>
    public void Record(WatchEventKind kind, string? reason)
    {
        if (reason is not (AttributionReasons.SaclMissing or AttributionReasons.AuditPolicyMissing))
        {
            return;
        }

        lock (_gate)
        {
            _uncoverable[kind] = timeProvider.GetUtcNow();
        }
    }

    /// <summary>The check for <paramref name="configuration"/>.</summary>
    public HealthCheckDto Evaluate(WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var now = timeProvider.GetUtcNow();
        List<WatchEventKind> kinds;
        lock (_gate)
        {
            foreach (var stale in _uncoverable.Where(p => now - p.Value > Window).Select(p => p.Key).ToList())
            {
                _uncoverable.Remove(stale);
            }

            kinds = [.. _uncoverable.Keys.Order()];
        }

        var args = new List<string>();
        foreach (var rule in (configuration.Rules ?? []).Where(r => r is { Enabled: true } && RuleMatcher.HasActorConditions(r)))
        {
            foreach (var kind in kinds.Where(k => (rule.Kinds ?? []).Count == 0 || rule.Kinds!.Contains(k)))
            {
                args.Add(rule.Id);
                args.Add(kind.ToString());
            }
        }

        return new HealthCheckDto
        {
            Code = CheckCode,
            State = args.Count == 0 ? HealthState.Ok : HealthState.Warning,
            Detail = args.Count == 0 ? null : "Rules use actor conditions for kinds whose actor cannot be determined (missing SACL or audit policy).",
            Args = args,
            CheckedAt = now,
        };
    }
}
