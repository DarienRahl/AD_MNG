using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// The language-specific content of one alert, shared by the e-mail, Teams card and HTML page formatters (section
/// 14.3): banner, sentence, facts, changes, links and footer – all texts already safe (sanitized directory strings,
/// not yet encoded for the output format).
/// </summary>
public sealed record AlertContent
{
    public required WatchEvent Event { get; init; }

    public required Severity Severity { get; init; }

    /// <summary>Upper-case severity word ("KRYTYCZNE").</summary>
    public required string SeverityWord { get; init; }

    /// <summary>The banner category: the rule name, else the kind title.</summary>
    public required string Category { get; init; }

    /// <summary>The kind title ("Dodano do grupy uprzywilejowanej").</summary>
    public required string Title { get; init; }

    /// <summary>The object as shown in the card header ("Jan Kowalski (jan.kowalski)").</summary>
    public required string ObjectName { get; init; }

    /// <summary>One sentence describing the change (passive voice in Polish).</summary>
    public required string Sentence { get; init; }

    /// <summary>"(członkostwo pośrednie: A → B)" for privilege changes, else null.</summary>
    public string? Via { get; init; }

    public IReadOnlyList<AlertFact> Facts { get; init; } = [];

    public IReadOnlyList<AlertChangeRow> Changes { get; init; } = [];

    /// <summary>Remedy of an unresolved actor or source, or null.</summary>
    public string? Remedy { get; init; }

    /// <summary>The HTML event page link (only with <c>PublicBaseUrl</c>).</summary>
    public string? EventLink { get; init; }

    /// <summary>"W AD_MNG: Narzędzia → Watchdog → zdarzenie nr 18342".</summary>
    public required string AdMngHint { get; init; }

    /// <summary>"Reguła … · zdarzenie … · usługa … · wiadomość automatyczna, nie odpowiadaj".</summary>
    public required string Footer { get; init; }

    /// <summary>The actor for one-line summaries (subject, fallback text), or null.</summary>
    public string? ActorShort { get; init; }

    /// <summary>The actor row value (also when unresolved), or null when the row is not shown.</summary>
    public string? Actor { get; init; }

    /// <summary>The event time ("14.10.2026 14:32:05 (UTC+02:00)").</summary>
    public required string When { get; init; }
}
