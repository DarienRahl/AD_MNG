using AdMng.Core.Model;
using AdMng.Presentation.Comparison;

namespace AdMng.Presentation.Settings;

/// <summary>
/// What the comparison window remembers (design note docs/dev/multi-window.md 8.3): the "Dopasuj według" rule per object
/// kind and the three switches. Names of rules and switches only - never DNs or values (rule 1).
/// </summary>
public sealed class ComparisonSettings
{
    /// <summary>The rule last chosen per object kind (missing: automatic).</summary>
    public Dictionary<ObjectKind, ObjectMatchRule> Rules { get; set; } = [];

    /// <summary>"Tylko różnice" (default on).</summary>
    public bool OnlyDifferences { get; set; } = true;

    /// <summary>"Pokaż atrybuty zmienne i identyfikujące" (default off).</summary>
    public bool ShowVolatile { get; set; }

    /// <summary>"Porównuj adresy bez domeny" (default on).</summary>
    public bool CompareAddressesWithoutDomain { get; set; } = true;

    /// <summary>The remembered rule for <paramref name="kind"/>, if it is one of the kind's choices; else automatic.</summary>
    public ObjectMatchRule RuleFor(ObjectKind kind) =>
        Rules.TryGetValue(kind, out var rule) && ObjectMatcher.Choices(kind).Contains(rule) ? rule : ObjectMatchRule.Automatic;

    /// <summary>Repairs a hand-edited or older settings file.</summary>
    public void Normalize()
    {
        Rules = (Rules ?? [])
            .Where(r => Enum.IsDefined(r.Key) && Enum.IsDefined(r.Value))
            .ToDictionary(r => r.Key, r => r.Value);
    }
}
