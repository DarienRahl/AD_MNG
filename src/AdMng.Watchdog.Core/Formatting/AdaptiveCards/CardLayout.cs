namespace AdMng.Watchdog.Core.Formatting.AdaptiveCards;

/// <summary>
/// The structure of a Teams card before serialization: a fixed header (title, counts), sections with removable rows,
/// an optional "Pokazano X z Y" line, actions and the fallback text. <see cref="CardBudget"/> drops rows from it until
/// the serialized message fits.
/// </summary>
public sealed class CardLayout
{
    /// <summary>Always kept (header container, counts).</summary>
    public List<AdaptiveElement> Header { get; } = [];

    public List<CardSection> Sections { get; } = [];

    /// <summary>Elements after the sections that are always kept.</summary>
    public List<AdaptiveElement> Footer { get; } = [];

    /// <summary>Total items the card stands for (Y of "Pokazano X z Y").</summary>
    public int TotalItems { get; set; }

    /// <summary>"Pokazano X z Y …" (shown, total); null = no such line. Written whenever not every item is shown.</summary>
    public Func<int, int, AdaptiveElement>? ShownLine { get; init; }

    /// <summary>Show the "Pokazano X z Y" line even when everything is shown (digest cards).</summary>
    public bool AlwaysShowShownLine { get; init; }

    /// <summary>Open-URL actions (the toggle actions of hidden rows are added automatically).</summary>
    public List<AdaptiveAction> Actions { get; } = [];

    /// <summary>Title of the toggle action of hidden rows ("Pokaż wszystkie zmiany").</summary>
    public string? ToggleTitle { get; init; }

    public required string FallbackText { get; init; }

    /// <summary>Items shown by all sections.</summary>
    public int ShownItems => Sections.Sum(s => s.ShownItems);

    /// <summary>The card body.</summary>
    public IEnumerable<AdaptiveElement> Body()
    {
        foreach (var element in Header)
        {
            yield return element;
        }

        foreach (var element in Sections.SelectMany(s => s.Elements()))
        {
            yield return element;
        }

        var shown = ShownItems;
        if (ShownLine is not null && (AlwaysShowShownLine || shown < TotalItems))
        {
            yield return ShownLine(shown, TotalItems);
        }

        foreach (var element in Footer)
        {
            yield return element;
        }
    }

    /// <summary>The card actions: a toggle for the hidden rows (when any), then <see cref="Actions"/>.</summary>
    public IEnumerable<AdaptiveAction> AllActions()
    {
        var hidden = Sections.Where(s => s.HiddenRows.Count > 0 && s.HiddenId is not null).Select(s => s.HiddenId!).ToList();
        if (hidden.Count > 0 && ToggleTitle is not null)
        {
            yield return new ToggleVisibilityAction(ToggleTitle, hidden);
        }

        foreach (var action in Actions)
        {
            yield return action;
        }
    }
}
