using System.Globalization;
using AdMng.Core.Pki;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Pki;

/// <summary>A summary chip: the findings of one severity ("Krytyczne: 3"); checking it filters the list to that severity.</summary>
public sealed partial class PkiSeverityChipViewModel(PkiFindingSeverity severity, string text, int count, Action<PkiSeverityChipViewModel, bool> toggled)
    : ObservableObject
{
    private bool _applying;

    public PkiFindingSeverity Severity { get; } = severity;

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

    /// <summary>The severity and count only.</summary>
    public override string ToString() => $"{Severity}: {Count.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>The summary chips, the filters (severity, ESC, area, template, text) and the list of the AD CS security review.</summary>
public sealed partial class PkiSecurityReviewDocumentViewModel
{
    private IReadOnlyList<PkiFindingRowViewModel> _all = [];
    private bool _applyingFilters;

    /// <summary>Ctrl+F: the view focuses the search box.</summary>
    public event EventHandler? SearchFocusRequested;

    /// <summary>The findings matching the filters, grouped by object (the most severe object first).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListNote))]
    public partial IReadOnlyList<PkiFindingRowViewModel> Rows { get; private set; } = [];

    /// <summary>The finding whose details are shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText), nameof(HasSelection), nameof(OpenObjectText))]
    [NotifyCanExecuteChangedFor(nameof(OpenTemplateCommand), nameof(OpenCaCommand), nameof(OpenObjectCommand), nameof(CopyCommand))]
    public partial PkiFindingRowViewModel? SelectedRow { get; set; }

    public bool HasSelection => SelectedRow is not null;

    /// <summary>The caption of <see cref="OpenObjectCommand"/>: <i>Otwórz konto / komputer / obiekt</i> of the selected finding.</summary>
    public string OpenObjectText => SelectedRow?.OpenObjectText ?? Localizer[L.PkiSecurity.OpenObject];

    /// <summary>One chip per severity with its count (Krytyczne … Informacje).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PkiSeverityChipViewModel> Chips { get; private set; }

    public IReadOnlyList<GpoOption<PkiFindingSeverity?>> SeverityOptions { get; private set; }

    [ObservableProperty]
    public partial GpoOption<PkiFindingSeverity?>? SelectedSeverity { get; set; }

    /// <summary>"Każda technika" and the ESC numbers of the review (ESC1 … ESC17).</summary>
    public IReadOnlyList<GpoOption<string?>> EscOptions { get; private set; }

    [ObservableProperty]
    public partial GpoOption<string?>? SelectedEsc { get; set; }

    public IReadOnlyList<GpoOption<PkiFindingArea?>> AreaOptions { get; private set; }

    [ObservableProperty]
    public partial GpoOption<PkiFindingArea?>? SelectedArea { get; set; }

    /// <summary>"Każdy szablon" and the templates the findings concern (their own or as a related target).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GpoOption<Guid?>> TemplateOptions { get; private set; }

    [ObservableProperty]
    public partial GpoOption<Guid?>? SelectedTemplate { get; set; }

    /// <summary>Search in the title, condition, object, principals, ESC and tool ids, area and code (case and diacritics ignored).</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>
    /// "Nie znaleziono podatnej konfiguracji…", "W tym lesie nie ma AD CS…", "Nie można ocenić PKI…" or "Żadna uwaga nie pasuje do
    /// filtrów." (null when the list has rows or nothing was checked).
    /// </summary>
    public string? ListNote => Rows.Count > 0 || _review is null ? null
        : _all.Count > 0 ? Localizer[L.PkiSecurity.NoMatches]
        : !_review.Result.IsEvaluated ? Localizer[L.PkiSecurity.NotEvaluatedList]
        : _review.Snapshot.Notices.Any(n => n.Code == PkiReadNoticeCode.BaseNotFound) ? Localizer[L.PkiSecurity.NoPki]
        : Localizer[L.PkiSecurity.NoFindings];

    /// <summary>A filter other than "everything" is set.</summary>
    public bool IsFiltered => SelectedSeverity?.Value is not null || SelectedEsc?.Value is not null || SelectedArea?.Value is not null
        || SelectedTemplate?.Value is not null || SearchText.Trim().Length > 0;

    /// <summary>Ctrl+F (<see cref="Shell.ISearchableDocument"/>).</summary>
    public void FocusSearch() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Shows the findings of <paramref name="code"/>'s ESC only (every other filter cleared; <see cref="PkiDocumentRequest.FindingCode"/>).
    /// An unknown code clears the filters.
    /// </summary>
    public void ShowCode(string? code)
    {
        var esc = code is not null && PkiFindingCodes.All.FirstOrDefault(d => d.Code == code) is { } definition ? definition.Esc : null;
        _applyingFilters = true;
        try
        {
            SelectedSeverity = SeverityOptions[0];
            SelectedArea = AreaOptions[0];
            SelectedTemplate = TemplateOptions.Count > 0 ? TemplateOptions[0] : null;
            SearchText = string.Empty;
            SelectedEsc = EscOptions.FirstOrDefault(o => o.Value == esc) ?? EscOptions[0];
        }
        finally
        {
            _applyingFilters = false;
        }

        ApplyFilters();
    }

    /// <summary>Esc in the search box.</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>"Wyczyść filtry": every severity, ESC, area and template, no search text.</summary>
    [RelayCommand]
    private void ClearFilters() => ShowCode(null);

    partial void OnSelectedSeverityChanged(GpoOption<PkiFindingSeverity?>? value)
    {
        foreach (var chip in Chips)
        {
            chip.Reflect(value?.Value == chip.Severity);
        }

        FiltersChanged();
    }

    partial void OnSelectedEscChanged(GpoOption<string?>? value) => FiltersChanged();

    partial void OnSelectedAreaChanged(GpoOption<PkiFindingArea?>? value) => FiltersChanged();

    partial void OnSelectedTemplateChanged(GpoOption<Guid?>? value) => FiltersChanged();

    partial void OnSearchTextChanged(string value) => FiltersChanged();

    partial void OnSelectedRowChanged(PkiFindingRowViewModel? value) => OnPropertyChanged(nameof(StatusText));

    private void FiltersChanged()
    {
        if (!_applyingFilters)
        {
            ApplyFilters();
        }
    }

    /// <summary>A chip was checked (filter to its severity) or unchecked (every severity).</summary>
    private void OnChipToggled(PkiSeverityChipViewModel chip, bool selected) =>
        SelectedSeverity = SeverityOptions.FirstOrDefault(o => selected ? o.Value == chip.Severity : o.Value is null);

    /// <summary>The rows matching the filters; the selection stays on the same finding (or near its place) when it can.</summary>
    private void ApplyFilters((string Key, int Index)? restore = null)
    {
        var severity = SelectedSeverity?.Value;
        var esc = SelectedEsc?.Value;
        var area = SelectedArea?.Value;
        var template = SelectedTemplate?.Value;
        var search = SearchText.Trim();
        // The invariant collation folds the Polish letters too (the Polish one keeps ś apart from s): "pewnosci" finds "pewności".
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        bool Has(string text) => compare.IndexOf(text, search, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
        var previous = restore ?? (SelectedRow is { } selected ? (selected.Key, IndexOf(Rows, selected)) : null);
        Rows = [.. _all.Where(r => (severity is null || r.Severity == severity)
            && (esc is null || string.Equals(r.Esc, esc, StringComparison.Ordinal))
            && (area is null || r.Area == area)
            && (template is null || r.Finding.ObjectId == template || r.Finding.RelatedObjectIds.Contains(template.Value))
            && (search.Length == 0 || Has(r.SearchText)))];
        SelectedRow = previous is { } keep
            ? Rows.FirstOrDefault(r => string.Equals(r.Key, keep.Key, StringComparison.Ordinal))
                ?? (Rows.Count == 0 || keep.Index < 0 ? null : Rows[Math.Min(keep.Index, Rows.Count - 1)])
            : null;
        LoadedText = _review is null ? LoadedText
            : IsFiltered ? Localizer.Format(L.PkiSecurity.FindingsShown, Rows.Count, _all.Count)
            : CorrespondenceText.Plural(Localizer, _all.Count, L.PkiSecurity.FindingsOne, L.PkiSecurity.FindingsFew, L.PkiSecurity.FindingsMany);
        OnPropertyChanged(nameof(ListNote));
        OnPropertyChanged(nameof(IsFiltered));
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The position of <paramref name="row"/> in <paramref name="rows"/> (-1 when it is not there).</summary>
    private static int IndexOf(IReadOnlyList<PkiFindingRowViewModel> rows, PkiFindingRowViewModel row)
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
        Chips = [.. PkiFindingText.Severities.Select(s =>
        {
            var count = _all.Count(r => r.Severity == s);
            var chip = new PkiSeverityChipViewModel(s, Localizer.Format(PkiTextKeys.ChipKey(s), count), count, OnChipToggled);
            chip.Reflect(selected == s);
            return chip;
        })];
    }

    /// <summary>The severity, ESC and area options in the UI language (the selections are kept).</summary>
    private void BuildFilterOptions()
    {
        var severity = SelectedSeverity?.Value;
        var esc = SelectedEsc?.Value;
        var area = SelectedArea?.Value;
        _applyingFilters = true;
        try
        {
            SeverityOptions = [new GpoOption<PkiFindingSeverity?>(null, Localizer[L.PkiSecurity.AllSeverities]),
                .. PkiFindingText.Severities.Select(s => new GpoOption<PkiFindingSeverity?>(s, PkiFindingText.Severity(Localizer, s)))];
            EscOptions = [new GpoOption<string?>(null, Localizer[L.PkiSecurity.AllEsc]),
                .. PkiFindingCodes.All.Select(d => d.Esc).Distinct(StringComparer.Ordinal).Select(e => new GpoOption<string?>(e, e))];
            AreaOptions = [new GpoOption<PkiFindingArea?>(null, Localizer[L.PkiSecurity.AllAreas]),
                .. Enum.GetValues<PkiFindingArea>().Select(a => new GpoOption<PkiFindingArea?>(a, PkiFindingText.Area(Localizer, a)))];
            OnPropertyChanged(nameof(SeverityOptions));
            OnPropertyChanged(nameof(EscOptions));
            OnPropertyChanged(nameof(AreaOptions));
            SelectedSeverity = SeverityOptions.First(o => o.Value == severity);
            SelectedEsc = EscOptions.FirstOrDefault(o => o.Value == esc) ?? EscOptions[0];
            SelectedArea = AreaOptions.First(o => o.Value == area);
        }
        finally
        {
            _applyingFilters = false;
        }
    }

    /// <summary>"Każdy szablon" and the templates of the findings by name (the selection is kept when the template still has findings).</summary>
    private void BuildTemplateOptions()
    {
        var template = SelectedTemplate?.Value;
        var compare = StringComparer.Create(Localizer.Culture, ignoreCase: true);
        var texts = _review?.Texts;
        _applyingFilters = true;
        try
        {
            TemplateOptions = [new GpoOption<Guid?>(null, Localizer[L.PkiSecurity.AllTemplates]),
                .. _all.SelectMany(r => r.Finding.RelatedObjectIds.Prepend(r.Finding.ObjectId ?? Guid.Empty))
                    .Where(id => id != Guid.Empty && texts?.TemplateOf(id) is not null)
                    .Distinct()
                    .Select(id => new GpoOption<Guid?>(id, texts!.NameOf(id)!))
                    .OrderBy(o => o.Text, compare)];
            SelectedTemplate = TemplateOptions.FirstOrDefault(o => o.Value == template) ?? TemplateOptions[0];
        }
        finally
        {
            _applyingFilters = false;
        }
    }
}
