using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Suggested (never applied) rule exclusions (section 12.3) for <c>StatusDto.Suggestions</c>: detected Entra Connect /
/// sync accounts for <c>tier0-security</c> (<see cref="SyncAccountDetector"/>) and computer clean-up accounts for
/// <c>bursts</c> (BurstDetector). In memory only; thread-safe.
/// </summary>
public sealed class ExclusionSuggestionRegistry
{
    public const string EntraConnectSync = "entraConnectSync";
    public const string ComputerCleanupAccount = "computerCleanupAccount";

    private readonly Lock _gate = new();
    private readonly Dictionary<(string RuleId, string ActorSid), ExclusionSuggestionDto> _items = [];

    /// <summary>Current suggestions (rule id, then actor).</summary>
    public IReadOnlyList<ExclusionSuggestionDto> Current
    {
        get
        {
            lock (_gate)
            {
                return [.. _items.Values.OrderBy(s => s.RuleId, StringComparer.Ordinal).ThenBy(s => s.ActorSid, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Adds or updates one suggestion.</summary>
    public void Add(ExclusionSuggestionDto suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        lock (_gate)
        {
            _items[(suggestion.RuleId, suggestion.ActorSid)] = suggestion;
        }
    }

    /// <summary>Replaces every suggestion with this reason code.</summary>
    public void Replace(string reasonCode, IEnumerable<ExclusionSuggestionDto> suggestions)
    {
        ArgumentNullException.ThrowIfNull(reasonCode);
        ArgumentNullException.ThrowIfNull(suggestions);
        lock (_gate)
        {
            foreach (var key in _items.Where(i => i.Value.ReasonCode == reasonCode).Select(i => i.Key).ToList())
            {
                _items.Remove(key);
            }

            foreach (var suggestion in suggestions)
            {
                _items[(suggestion.RuleId, suggestion.ActorSid)] = suggestion;
            }
        }
    }
}
