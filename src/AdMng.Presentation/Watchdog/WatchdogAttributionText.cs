using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Events;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Presentation.Watchdog;

/// <summary>Texts of an event's attribution: who ("ustalanie…", "nieustalony (powód)") and from where (IP first).</summary>
public static class WatchdogAttributionText
{
    /// <summary>The actor column: account, "ustalanie…", "nieustalony (reason)", "niejednoznaczny: a, b", or empty.</summary>
    public static string Actor(ILocalizer localizer, EventAttribution? attribution)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        attribution ??= EventAttribution.Pending;
        return attribution.Status switch
        {
            AttributionStatus.Pending => localizer[L.Watchdog.ActorPending],
            AttributionStatus.Unknown => localizer.Format(L.Watchdog.ActorUnknown, localizer.ReasonText(attribution.Reason ?? AttributionReasons.NoMatch)),
            AttributionStatus.Ambiguous => localizer.Format(L.Watchdog.ActorAmbiguous, string.Join(", ", attribution.Candidates.Select(Principal))),
            AttributionStatus.Resolved => attribution.Actor is { } actor ? Principal(actor) : string.Empty,
            _ => string.Empty,
        };
    }

    /// <summary>The source column: "10.20.4.17 · WRO-LT014 (z 4740)" – IP first, the client-supplied name with its origin.</summary>
    public static string Source(ILocalizer localizer, EventAttribution? attribution)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (attribution is null || (attribution.IpAddress is null && attribution.Workstation is null))
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(attribution.IpAddress))
        {
            parts.Add(attribution.IpAddress);
        }

        if (!string.IsNullOrEmpty(attribution.Workstation))
        {
            parts.Add(attribution.Workstation);
        }

        var text = string.Join(" · ", parts);
        return string.IsNullOrEmpty(attribution.SourceMethod) ? text : $"{text} ({localizer.SourceOriginText(attribution.SourceMethod)})";
    }

    /// <summary>"CONTOSO\jan.kowalski", else the display name, the SID or the DN.</summary>
    public static string Principal(PrincipalRef principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.Account ?? principal.DisplayName ?? principal.Sid ?? principal.Dn ?? string.Empty;
    }

    /// <summary>"Jan Kowalski (CONTOSO\jan.kowalski, S-1-5-21-…)" for the details pane.</summary>
    public static string PrincipalDetails(PrincipalRef principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var details = string.Join(", ", new[] { principal.Account, principal.Sid }.Where(s => !string.IsNullOrEmpty(s)));
        return principal.DisplayName is { Length: > 0 } name && !string.Equals(name, principal.Account, StringComparison.OrdinalIgnoreCase)
            ? (details.Length == 0 ? name : $"{name} ({details})")
            : details;
    }
}
