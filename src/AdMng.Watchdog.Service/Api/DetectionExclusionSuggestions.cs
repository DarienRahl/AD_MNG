using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Detection;

namespace AdMng.Watchdog.Service.Api;

/// <summary>The <see cref="IExclusionSuggestionSource"/> of the service: the detection area's in-memory
/// <see cref="ExclusionSuggestionRegistry"/> (detected Entra Connect sync accounts for <c>tier0-security</c>, computer
/// clean-up accounts for <c>bursts</c>, spec 12.3) – shown in the status, never applied automatically.</summary>
public sealed class DetectionExclusionSuggestions(ExclusionSuggestionRegistry registry) : IExclusionSuggestionSource
{
    public IReadOnlyList<ExclusionSuggestionDto> Current => registry.Current;
}
