using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Service.Api;

/// <summary>Suggested rule exclusions shown by <c>/api/v1/status</c> (e.g. a detected Entra Connect sync account). The
/// detection area's registry is the source (<see cref="DetectionExclusionSuggestions"/>).</summary>
public interface IExclusionSuggestionSource
{
    IReadOnlyList<ExclusionSuggestionDto> Current { get; }
}
