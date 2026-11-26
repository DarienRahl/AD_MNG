using System.Globalization;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Search;

/// <summary>
/// One group of "Wyszukiwarka ustawień GPO": a setting (one base id) with every GPO that sets it – the header shows its name,
/// where GPMC lists it and "ustawione w N GPO – wartości zgodne / różne / nieporównywalne". In memory only;
/// <see cref="ToString"/> prints the area and counts only.
/// </summary>
public sealed class GpoSearchGroupViewModel
{
    internal GpoSearchGroupViewModel(ILocalizer localizer, GpoSearchUnitGroup group, Func<GpoSearchGroupViewModel, IReadOnlyList<GpoSearchRowViewModel>> rows)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        Source = group ?? throw new ArgumentNullException(nameof(group));
        var first = group.Occurrences[0];
        Name = first.Text.Name;
        GroupPath = first.Text.GroupPath;
        Area = first.Area;
        AreaText = localizer[L.GpoSearch.AreaKey(Area)];
        SummaryText = localizer.Format(L.GpoSearch.AgreementKey(group.Agreement), group.GpoCount.ToString(localizer.Culture));
        Rows = rows(this);
    }

    /// <summary>The occurrences of the corpus behind the group.</summary>
    public GpoSearchUnitGroup Source { get; }

    public string BaseId => Source.BaseId;

    /// <summary>The setting's name (of its first occurrence).</summary>
    public string Name { get; }

    /// <summary>"Konfiguracja komputera › Zasady › Ustawienia systemu Windows › … › Zasady haseł".</summary>
    public string GroupPath { get; }

    public GpoSearchArea Area { get; }

    public string AreaText { get; }

    public GpoValueAgreement Agreement => Source.Agreement;

    /// <summary>The applying occurrences differ (the header marks it).</summary>
    public bool HasDifferentValues => Source.Agreement == GpoValueAgreement.Different;

    public int GpoCount => Source.GpoCount;

    /// <summary>"ustawione w 2 GPO – wartości różne".</summary>
    public string SummaryText { get; }

    /// <summary>One row per occurrence, by GPO name.</summary>
    public IReadOnlyList<GpoSearchRowViewModel> Rows { get; }

    /// <summary>For diagnostics: the area and counts only (names are GPO content).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Area}: {Rows.Count} rows, {Agreement}");
}
