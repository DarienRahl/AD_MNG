namespace AdMng.Watchdog.Core.Formatting.AdaptiveCards;

/// <summary>
/// The 24 KiB limit of Teams messages (section 14.4): after serialization the UTF-8 size must not exceed
/// <see cref="MaxBytes"/>. Rows are dropped from the end of the largest section (hidden rows first) until the message
/// fits; the header, counts and "Pokazano X z Y" are always kept.
/// </summary>
public static class CardBudget
{
    /// <summary>Maximum UTF-8 size of the serialized message.</summary>
    public const int MaxBytes = 24_576;

    /// <summary>Serializes <paramref name="layout"/>, dropping rows until it fits into <paramref name="maxBytes"/>.</summary>
    /// <returns>The message bytes (may exceed the budget only when no row is left to drop).</returns>
    public static byte[] Fit(CardLayout layout, string? version = null, int maxBytes = MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var bytes = AdaptiveCardBuilder.Build(layout, version);
        while (bytes.Length > maxBytes)
        {
            var overshoot = bytes.Length - maxBytes;
            var removed = 0;
            while (removed < overshoot && DropOne(layout) is { } size)
            {
                removed += size;
            }

            if (removed == 0)
            {
                break;
            }

            bytes = AdaptiveCardBuilder.Build(layout, version);
        }

        return bytes;
    }

    /// <summary>Drops the last row of the section with the most rows; returns its estimated size or null when there is
    /// nothing left to drop.</summary>
    private static int? DropOne(CardLayout layout)
    {
        var section = layout.Sections
            .Where(s => s.Rows.Count + s.HiddenRows.Count > 0)
            .OrderByDescending(s => s.Rows.Count + s.HiddenRows.Count)
            .FirstOrDefault();
        if (section is null)
        {
            return null;
        }

        var list = section.HiddenRows.Count > 0 ? section.HiddenRows : section.Rows;
        var row = list[^1];
        list.RemoveAt(list.Count - 1);
        section.Dropped += row.Items;
        return AdaptiveCardBuilder.Size(row.Element) + 1;
    }
}
