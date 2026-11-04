using System.Globalization;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.Diagnostics;

/// <summary>A summary chip: the findings of one severity ("Wysokie: 3"); checking it filters the list to that severity.</summary>
public sealed partial class GpoSeverityChipViewModel(GpoFindingSeverity severity, string text, int count, Action<GpoSeverityChipViewModel, bool> toggled)
    : ObservableObject
{
    private bool _applying;

    public GpoFindingSeverity Severity { get; } = severity;

    public string Text { get; } = text;

    public int Count { get; } = count;

    public bool HasFindings => Count > 0;

    /// <summary>The list shows only this severity.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Sets <see cref="IsSelected"/> without filtering again (the filter changed elsewhere).</summary>
    internal void Reflect(bool selected)
    {
        _applying = true;
        try
        {
            IsSelected = selected;
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_applying)
        {
            toggled(this, value);
        }
    }
}

/// <summary>The summary chips, the filters (severity, area, GPO, text) and the list of "Diagnostyka zasad grupy".</summary>
public sealed partial class GroupPolicyDiagnosticsDocumentViewModel
{
    private IReadOnlyList<GpoFindingRowViewModel> _all = [];

    /// <summary>Ctrl+F: the view focuses the search box.</summary>
    public event EventHandler? SearchFocusRequested;

    /// <summary>The findings matching the filters, most severe first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListNote))]
    public partial IReadOnlyList<GpoFindingRowViewModel> Rows { get; private set; } = [];

    /// <summary>The finding whose details are shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText), nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(FixCommand), nameof(OpenGpoCommand), nameof(EditSettingCommand), nameof(ShowInModelCommand), nameof(CopyCommand))]
    public partial GpoFindingRowViewModel? SelectedRow { get; set; }

    public bool HasSelection => SelectedRow is not null;

    /// <summary>One chip per severity with its count (Krytyczne … Informacyjne).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GpoSeverityChipViewModel> Chips { get; private set; }

    public IReadOnlyList<GpoOption<GpoFindingSeverity?>> SeverityOptions { get; private set; }

    [ObservableProperty]
    public partial GpoOption<GpoFindingSeverity?>? SelectedSeverity { get; set; }

    public IReadOnlyList<GpoOption<GpoFindingArea?>> AreaOptions { get; private set; }

    /// <summary>The area (Bezpieczeństwo is one of them).</summary>
    [ObservableProperty]
    public partial GpoOption<GpoFindingArea?>? SelectedArea { get; set; }

    /// <summary>"Każdy obiekt GPO" and the GPOs that have findings.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GpoOption<Guid?>> GpoOptions { get; private set; }

    [ObservableProperty]
    public partial GpoOption<Guid?>? SelectedGpo { get; set; }

    /// <summary>Search in the title, message, GPO, container or setting, area and code (case and diacritics ignored).</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>"Nie znaleziono problemów." / "Żadna uwaga nie pasuje do filtrów." (null when the list has rows or nothing was checked).</summary>
    public string? ListNote => Rows.Count > 0 || _check is null ? null
        : _all.Count == 0 ? Localizer[L.GpoDiagnostics.NoFindings] : Localizer[L.GpoDiagnostics.NoMatches];

    /// <summary>A filter other than "everything" is set.</summary>
    public bool IsFiltered => SelectedSeverity?.Value is not null || SelectedArea?.Value is not null || SelectedGpo?.Value is not null
        || SearchText.Trim().Length > 0;

    /// <summary>Ctrl+F (<see cref="Shell.ISearchableDocument"/>).</summary>
    public void FocusSearch() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Esc in the search box.</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>"Wyczyść filtry": every severity, area and GPO, no search text.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        _applyingFilters = true;
        try
        {
            SelectedSeverity = SeverityOptions[0];
            SelectedArea = AreaOptions[0];
            SelectedGpo = GpoOptions.Count > 0 ? GpoOptions[0] : null;
            SearchText = string.Empty;
        }
        finally
        {
            _applyingFilters = false;
        }

        ApplyFilters();
    }

    private bool _applyingFilters;

    partial void OnSelectedSeverityChanged(GpoOption<GpoFindingSeverity?>? value)
    {
        foreach (var chip in Chips)
        {
            chip.Reflect(value?.Value == chip.Severity);
        }

        FiltersChanged();
    }

    partial void OnSelectedAreaChanged(GpoOption<GpoFindingArea?>? value) => FiltersChanged();

    partial void OnSelectedGpoChanged(GpoOption<Guid?>? value) => FiltersChanged();

    partial void OnSearchTextChanged(string value) => FiltersChanged();

    partial void OnSelectedRowChanged(GpoFindingRowViewModel? value) => OnPropertyChanged(nameof(StatusText));

    private void FiltersChanged()
    {
        if (!_applyingFilters)
        {
            ApplyFilters();
        }
    }

    /// <summary>A chip was checked (filter to its severity) or unchecked (every severity).</summary>
    private void OnChipToggled(GpoSeverityChipViewModel chip, bool selected) =>
        SelectedSeverity = SeverityOptions.FirstOrDefault(o => selected ? o.Value == chip.Severity : o.Value is null);

    /// <summary>The rows matching the filters; the selection stays on the same finding (or near its place) when it can.</summary>
    private void ApplyFilters((string Key, int Index)? restore = null)
    {
        var severity = SelectedSeverity?.Value;
        var area = SelectedArea?.Value;
        var gpo = SelectedGpo?.Value;
        var search = SearchText.Trim();
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        bool Has(string text) => compare.IndexOf(text, search, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
        var previous = restore ?? (SelectedRow is { } selected ? (selected.Key, IndexOf(Rows, selected)) : null);
        Rows = [.. _all.Where(r => (severity is null || r.Severity == severity)
            && (area is null || r.Area == area)
            && (gpo is null || r.Finding.GpoId == gpo || r.Finding.RelatedGpoIds.Contains(gpo.Value))
            && (search.Length == 0 || Has(r.SearchText)))];
        SelectedRow = previous is { } keep
            ? Rows.FirstOrDefault(r => string.Equals(r.Key, keep.Key, StringComparison.Ordinal))
                ?? (Rows.Count == 0 || keep.Index < 0 ? null : Rows[Math.Min(keep.Index, Rows.Count - 1)])
            : null;
        LoadedText = _check is null ? LoadedText
            : IsFiltered ? Localizer.Format(L.GpoDiagnostics.FindingsShown, Rows.Count, _all.Count)
            : Correspondence.CorrespondenceText.Plural(Localizer, _all.Count, L.GpoDiagnostics.FindingsOne, L.GpoDiagnostics.FindingsFew, L.GpoDiagnostics.FindingsMany);
        OnPropertyChanged(nameof(ListNote));
        OnPropertyChanged(nameof(IsFiltered));
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The position of <paramref name="row"/> in <paramref name="rows"/> (-1 when it is not there).</summary>
    private static int IndexOf(IReadOnlyList<GpoFindingRowViewModel> rows, GpoFindingRowViewModel row)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The chips of the shown check.</summary>
    private void UpdateSummary()
    {
        var selected = SelectedSeverity?.Value;
        Chips = [.. GpoFindingText.Severities.Select(s =>
        {
            var count = _all.Count(r => r.Severity == s);
            var chip = new GpoSeverityChipViewModel(s, Localizer.Format(GpoFindingText.ChipKey(s), count), count, OnChipToggled);
            chip.Reflect(selected == s);
            return chip;
        })];
    }

    /// <summary>The severity and area options in the UI language (the selections are kept).</summary>
    private void BuildFilterOptions()
    {
        var severity = SelectedSeverity?.Value;
        var area = SelectedArea?.Value;
        _applyingFilters = true;
        try
        {
            SeverityOptions = [new GpoOption<GpoFindingSeverity?>(null, Localizer[L.GpoDiagnostics.AllSeverities]),
                .. GpoFindingText.Severities.Select(s => new GpoOption<GpoFindingSeverity?>(s, GpoFindingText.Severity(Localizer, s)))];
            AreaOptions = [new GpoOption<GpoFindingArea?>(null, Localizer[L.GpoDiagnostics.AllAreas]),
                .. Enum.GetValues<GpoFindingArea>().Select(a => new GpoOption<GpoFindingArea?>(a, GpoFindingText.Area(Localizer, a)))];
            OnPropertyChanged(nameof(SeverityOptions));
            OnPropertyChanged(nameof(AreaOptions));
            SelectedSeverity = SeverityOptions.First(o => o.Value == severity);
            SelectedArea = AreaOptions.First(o => o.Value == area);
        }
        finally
        {
            _applyingFilters = false;
        }
    }

    /// <summary>"Każdy obiekt GPO" and the GPOs of the findings by name (the selection is kept when the GPO still has findings).</summary>
    private void BuildGpoOptions()
    {
        var gpo = SelectedGpo?.Value;
        var compare = StringComparer.Create(Localizer.Culture, ignoreCase: true);
        _applyingFilters = true;
        try
        {
            GpoOptions = [new GpoOption<Guid?>(null, Localizer[L.GpoDiagnostics.AllGpos]),
                .. _all.Where(r => r.Finding.GpoId is not null)
                    .GroupBy(r => r.Finding.GpoId!.Value)
                    .Select(g => new GpoOption<Guid?>(g.Key, g.First().GpoText))
                    .OrderBy(o => o.Text, compare)];
            SelectedGpo = GpoOptions.FirstOrDefault(o => o.Value == gpo) ?? GpoOptions[0];
        }
        finally
        {
            _applyingFilters = false;
        }
    }
}
