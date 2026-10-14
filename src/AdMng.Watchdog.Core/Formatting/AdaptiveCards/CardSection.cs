namespace AdMng.Watchdog.Core.Formatting.AdaptiveCards;

/// <summary>
/// A section of a <see cref="CardLayout"/>: a fixed heading, visible rows, rows hidden behind a toggle ("Pokaż
/// wszystkie zmiany") and a "… i N kolejnych" line for rows that were never added (<see cref="Omitted"/>) or dropped by
/// <see cref="CardBudget"/> (<see cref="Dropped"/>).
/// </summary>
public sealed class CardSection
{
    /// <summary>Fixed elements (title, column headers) – never dropped.</summary>
    public List<AdaptiveElement> Heading { get; } = [];

    public List<CardRow> Rows { get; } = [];

    /// <summary>Rows inside a hidden container toggled by <see cref="HiddenId"/>.</summary>
    public List<CardRow> HiddenRows { get; } = [];

    /// <summary>Element id of the hidden container (unique within the card).</summary>
    public string? HiddenId { get; init; }

    /// <summary>Items left out by the formatter (e.g. beyond <c>MaxRowsPerSection</c>).</summary>
    public int Omitted { get; set; }

    /// <summary>Items dropped to fit the 24 KiB budget.</summary>
    public int Dropped { get; set; }

    /// <summary>The "… i N kolejnych" line for <c>Omitted + Dropped</c> items; null = no such line.</summary>
    public Func<int, AdaptiveElement>? MoreLine { get; init; }

    /// <summary>Items shown (visible and hidden rows).</summary>
    public int ShownItems => Rows.Sum(r => r.Items) + HiddenRows.Sum(r => r.Items);

    /// <summary>The body elements of this section.</summary>
    public IEnumerable<AdaptiveElement> Elements()
    {
        foreach (var element in Heading)
        {
            yield return element;
        }

        foreach (var row in Rows)
        {
            yield return row.Element;
        }

        if (HiddenRows.Count > 0 && HiddenId is not null)
        {
            yield return new Container([.. HiddenRows.Select(r => r.Element)]) { Id = HiddenId, IsVisible = false };
        }

        if (Omitted + Dropped > 0 && MoreLine is not null)
        {
            yield return MoreLine(Omitted + Dropped);
        }
    }
}
