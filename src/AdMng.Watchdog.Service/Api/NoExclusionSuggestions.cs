using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Service.Api;

/// <summary>The empty <see cref="IExclusionSuggestionSource"/>.</summary>
public sealed class NoExclusionSuggestions : IExclusionSuggestionSource
{
    public IReadOnlyList<ExclusionSuggestionDto> Current => [];
}
