using AdMng.Watchdog.Contracts.Events;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Steps 3 and 4 of the severity resolution (section 12.2) when an attribution becomes final (11.4 #8): the
/// attribution-dependent kinds are re-resolved, then the explicit per-kind overrides are applied again (they come last
/// and may lower). Steps 1–2 (catalog default, tag bumps) happened at detection (<c>Detection/SeverityResolver</c>);
/// the stored severity is their result (with the provisional value of step 3).
/// <list type="bullet">
/// <item>PasswordChanged of a privileged or service account: High for tag <c>passwordReset</c> or an attribution ending
/// Unknown/Ambiguous, Info for <c>selfChange</c> (the user's own change at expiry).</item>
/// <item>AdminCountSet still High (the principal was not privileged): Info when the actor is SYSTEM (SDProp).</item>
/// <item>KeyCredentialChanged of a user that is not privileged: High when the actor is resolved and is neither the
/// user itself nor a sync account.</item>
/// <item>Override: PasswordChanged of krbtgt is exactly Medium.</item>
/// </list>
/// </summary>
public static class AttributionSeverityPolicy
{
    /// <summary>Kinds whose severity depends on the attribution (the planner holds them until it is final).</summary>
    public static IReadOnlySet<WatchEventKind> AttributionDependentKinds { get; } =
        new HashSet<WatchEventKind> { WatchEventKind.PasswordChanged, WatchEventKind.AdminCountSet, WatchEventKind.KeyCredentialChanged };

    /// <summary>The final severity of <paramref name="watchEvent"/> with <paramref name="attribution"/> and the tags it
    /// carries after attribution; null when unchanged.</summary>
    public static Severity? Resolve(WatchEvent watchEvent, EventAttribution attribution, IReadOnlyCollection<string> tags)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(attribution);
        ArgumentNullException.ThrowIfNull(tags);
        bool Has(string tag) => tags.Contains(tag, StringComparer.OrdinalIgnoreCase);

        var severity = watchEvent.Severity;
        switch (watchEvent.Kind)
        {
            case WatchEventKind.PasswordChanged when Has(EventTags.Privileged) || Has(EventTags.ServiceAccount):
                if (Has(EventTags.SelfChange))
                {
                    severity = Severity.Info;
                }
                else if (Has(EventTags.PasswordReset) || attribution.Status is AttributionStatus.Unknown or AttributionStatus.Ambiguous)
                {
                    severity = Severity.High;
                }

                break;
            case WatchEventKind.AdminCountSet when severity >= Severity.High && attribution.Reason == AttributionReasons.System:
                severity = Severity.Info;
                break;
            case WatchEventKind.KeyCredentialChanged when watchEvent.Object.Kind == WatchObjectKind.User && !Has(EventTags.Privileged):
                var resolved = attribution.Status == AttributionStatus.Resolved && attribution.Actor?.Sid is not null;
                var self = string.Equals(attribution.Actor?.Sid, watchEvent.Object.Sid, StringComparison.OrdinalIgnoreCase);
                if (resolved && !self && !Has(EventTags.SyncAccount) && attribution.Reason != AttributionReasons.System)
                {
                    severity = severity < Severity.High ? Severity.High : severity;
                }

                break;
        }

        if (watchEvent.Kind == WatchEventKind.PasswordChanged && Has(EventTags.Krbtgt))
        {
            severity = Severity.Medium;
        }

        return severity == watchEvent.Severity ? null : severity;
    }
}
