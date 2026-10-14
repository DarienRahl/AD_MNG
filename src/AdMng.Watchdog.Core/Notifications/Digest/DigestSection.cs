namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>One digest section: counters, the first <c>MaxRowsPerSection</c> rows and the number of items in total
/// ("… i N kolejnych – pełna lista w zakładce Watchdog").</summary>
public sealed record DigestSection
{
    public required DigestSectionKind Kind { get; init; }

    public IReadOnlyList<DigestFact> Facts { get; init; } = [];

    public IReadOnlyList<DigestRow> Rows { get; init; } = [];

    /// <summary>Rows of the section in total (shown or not).</summary>
    public int Total { get; init; }

    /// <summary>Rows not shown.</summary>
    public int MoreCount => Math.Max(0, Total - Rows.Count);
}
