using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Steps 3 and 4 of the severity resolution (section 12.2) when an attribution becomes final (11.4 #8), as the
/// attribution engine stores them (<c>AttributionUpdate.Severity</c>, null = unchanged). The rules themselves live in
/// <see cref="SeverityResolver.ApplyAttribution"/> – the one implementation of 12.2 – applied to the stored severity
/// (steps 1–2 happened at detection, with the provisional value of step 3):
/// <list type="bullet">
/// <item>PasswordChanged of a privileged or service account: High for tag <c>passwordReset</c> or an attribution ending
/// Unknown/Ambiguous, Info for <c>selfChange</c> (the user's own change at expiry).</item>
/// <item>AdminCountSet of a principal that is not privileged: Info when the actor is SYSTEM (SDProp), else High.</item>
/// <item>KeyCredentialChanged of a user that is not privileged: High when the actor is resolved and is neither the
/// user itself nor a sync account, else Medium.</item>
/// <item>Override: PasswordChanged of krbtgt is exactly Medium.</item>
/// </list>
/// </summary>
public static class AttributionSeverityPolicy
{
    /// <summary>Kinds whose severity depends on the attribution (<see cref="SeverityResolver.AttributionDependentKinds"/>).</summary>
    public static IReadOnlySet<WatchEventKind> AttributionDependentKinds => SeverityResolver.AttributionDependentKinds;

    /// <summary>The final severity of <paramref name="watchEvent"/> with <paramref name="attribution"/> and the tags it
    /// carries after attribution; null when unchanged.</summary>
    public static Severity? Resolve(WatchEvent watchEvent, EventAttribution attribution, IReadOnlyCollection<string> tags)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(attribution);
        ArgumentNullException.ThrowIfNull(tags);
        var severity = SeverityResolver.ApplyAttribution(watchEvent, watchEvent.Severity, [.. tags], attribution);
        return severity == watchEvent.Severity ? null : severity;
    }
}
