using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Watchdog;

/// <summary>A suggested (never applied) exclusion from the status, e.g. a detected Entra Connect sync account.</summary>
public sealed class WatchdogSuggestionViewModel(ExclusionSuggestionDto suggestion, ILocalizer localizer)
{
    public ExclusionSuggestionDto Suggestion { get; } = suggestion ?? throw new ArgumentNullException(nameof(suggestion));

    /// <summary>"Konto synchronizacji Entra Connect: CONTOSO\MSOL_… (reguła tier0-security)".</summary>
    public string Text { get; } = (localizer ?? throw new ArgumentNullException(nameof(localizer))).Format(
        L.Watchdog.SuggestionText,
        localizer.SuggestionText(suggestion.ReasonCode),
        suggestion.ActorAccount ?? suggestion.ActorSid,
        suggestion.RuleId);
}
