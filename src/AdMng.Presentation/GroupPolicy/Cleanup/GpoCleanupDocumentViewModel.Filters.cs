using System.Globalization;
using AdMng.Core.GroupPolicy.Cleanup;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.Cleanup;

/// <summary>A summary chip: the proposals of one kind ("Puste obiekty GPO (2)"); checking it filters the list to that kind.</summary>
public sealed partial class GpoCleanupChipViewModel(GpoCleanupKind kind, string text, int count, Action<GpoCleanupChipViewModel, bool> toggled)
    : ObservableObject
{
    private bool _applying;

    public GpoCleanupKind Kind { get; } = kind;

    public string Text { get; } = text;

    public int Count { get; } = count;

    public bool HasProposals => Count > 0;

    /// <summary>The list shows only this kind.</summary>
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

/// <summary>The summary chips, the filters (kind, GPO, text) and the list of "Porządki w GPO".</summary>
public sealed partial class GpoCleanupDocumentViewModel
{
    private IReadOnlyList<GpoCleanupRowViewModel> _all = [];
    private bool _applyingFilters;

    /// <summary>Ctrl+F: the view focuses the search box.</summary>
    public event EventHandler? SearchFocusRequested;

    /// <summary>The proposals matching the filters (by kind, then GPO name).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListNote))]
    public partial IReadOnlyList<GpoCleanupRowViewModel> Rows { get; private set; } = [];

    /// <summary>The proposal whose details are shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText), nameof(HasSelection), nameof(ShowsDelete), nameof(ShowsUnlink), nameof(ShowsDisable),
        nameof(ShowsRemoveSetting), nameof(ShowsCopyTo), nameof(ShowsMerge), nameof(ShowsNoActions))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(UnlinkCommand), nameof(DisableCommand), nameof(RemoveSettingCommand), nameof(CopyToCommand),
        nameof(MergeCommand), nameof(OpenGpoCommand), nameof(CompareCommand), nameof(ShowInSearchCommand), nameof(CopyCommand))]
    public partial GpoCleanupRowViewModel? SelectedRow { get; set; }

    public bool HasSelection => SelectedRow is not null;

    /// <summary>The selected proposal offers "Usuń GPO…".</summary>
    public bool ShowsDelete => SelectedRow?.Offers(GpoCleanupAction.Delete) == true;

    public bool ShowsUnlink => SelectedRow?.Offers(GpoCleanupAction.Unlink) == true;

    public bool ShowsDisable => SelectedRow?.Offers(GpoCleanupAction.Disable) == true;

    public bool ShowsRemoveSetting => SelectedRow?.Offers(GpoCleanupAction.RemoveSetting) == true;

    public bool ShowsCopyTo => SelectedRow?.Offers(GpoCleanupAction.CopyTo) == true;

    public bool ShowsMerge => SelectedRow?.Offers(GpoCleanupAction.Merge) == true;

    /// <summary>The selected proposal is informational (no action): "tylko informacja".</summary>
    public bool ShowsNoActions => SelectedRow is { HasActions: false };

    /// <summary>One chip per kind with its count.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GpoCleanupChipViewModel> Chips { get; private set; }

    public IReadOnlyList<GpoOption<GpoCleanupKind?>> KindOptions { get; private set; }

    [ObservableProperty]
    public partial GpoOption<GpoCleanupKind?>? SelectedKind { get; set; }

    /// <summary>"Każdy obiekt GPO" and the GPOs the proposals are about.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GpoOption<Guid?>> GpoOptions { get; private set; }

    [ObservableProperty]
    public partial GpoOption<Guid?>? SelectedGpo { get; set; }

    /// <summary>Search in the kind, GPOs, setting, message and notes (case and diacritics ignored).</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>"Nie znaleziono nic do uporządkowania." / "Żadna propozycja nie pasuje do filtrów." (null when there are rows or no analysis).</summary>
    public string? ListNote => Rows.Count > 0 || _shown is null ? null
        : _all.Count == 0 ? Localizer[L.GpoCleanup.NoProposals] : Localizer[L.GpoCleanup.NoMatches];

    /// <summary>A filter other than "everything" is set.</summary>
    public bool IsFiltered => SelectedKind?.Value is not null || SelectedGpo?.Value is not null || SearchText.Trim().Length > 0;

    /// <summary>Ctrl+F (<see cref="Shell.ISearchableDocument"/>).</summary>
    public void FocusSearch() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Esc in the search box.</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>"Wyczyść filtry": every kind and GPO, no search text.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        _applyingFilters = true;
        try
        {
            SelectedKind = KindOptions[0];
            SelectedGpo = GpoOptions.Count > 0 ? GpoOptions[0] : null;
            SearchText = string.Empty;
        }
        finally
        {
            _applyingFilters = false;
        }

        ApplyFilters();
    }

    partial void OnSelectedKindChanged(GpoOption<GpoCleanupKind?>? value)
    {
        foreach (var chip in Chips)
        {
            chip.Reflect(value?.Value == chip.Kind);
        }

        FiltersChanged();
    }

    partial void OnSelectedGpoChanged(GpoOption<Guid?>? value) => FiltersChanged();

    partial void OnSearchTextChanged(string value) => FiltersChanged();

    partial void OnSelectedRowChanged(GpoCleanupRowViewModel? value) => OnPropertyChanged(nameof(StatusText));

    private void FiltersChanged()
    {
        if (!_applyingFilters)
        {
            ApplyFilters();
        }
    }

    /// <summary>A chip was checked (filter to its kind) or unchecked (every kind).</summary>
    private void OnChipToggled(GpoCleanupChipViewModel chip, bool selected) =>
        SelectedKind = KindOptions.FirstOrDefault(o => selected ? o.Value == chip.Kind : o.Value is null);

    /// <summary>The rows matching the filters; the selection stays on the same proposal (or near its place) when it can.</summary>
    private void ApplyFilters((string Key, int Index)? restore = null)
    {
        var kind = SelectedKind?.Value;
        var gpo = SelectedGpo?.Value;
        var search = SearchText.Trim();
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        bool Has(string text) => compare.IndexOf(text, search, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
        var previous = restore ?? (SelectedRow is { } selected ? (selected.Key, IndexOf(Rows, selected)) : null);
        Rows = [.. _all.Where(r => (kind is null || r.Kind == kind)
            && (gpo is null || r.GpoId == gpo || r.Proposal.RelatedGpoIds.Contains(gpo.Value))
            && (search.Length == 0 || Has(r.SearchText)))];
        SelectedRow = previous is { } keep
            ? Rows.FirstOrDefault(r => string.Equals(r.Key, keep.Key, StringComparison.Ordinal))
                ?? (Rows.Count == 0 || keep.Index < 0 ? null : Rows[Math.Min(keep.Index, Rows.Count - 1)])
            : null;
        LoadedText = _shown is null ? LoadedText
            : IsFiltered ? Localizer.Format(L.GpoCleanup.ProposalsShown, Rows.Count, _all.Count)
            : Correspondence.CorrespondenceText.Plural(Localizer, _all.Count, L.GpoCleanup.ProposalsOne, L.GpoCleanup.ProposalsFew, L.GpoCleanup.ProposalsMany);
        OnPropertyChanged(nameof(ListNote));
        OnPropertyChanged(nameof(IsFiltered));
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The position of <paramref name="row"/> in <paramref name="rows"/> (-1 when it is not there).</summary>
    private static int IndexOf(IReadOnlyList<GpoCleanupRowViewModel> rows, GpoCleanupRowViewModel row)
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

    /// <summary>The chips of the shown analysis (one per kind).</summary>
    private void UpdateSummary()
    {
        var selected = SelectedKind?.Value;
        Chips = [.. Enum.GetValues<GpoCleanupKind>().Select(k =>
        {
            var count = _all.Count(r => r.Kind == k);
            var chip = new GpoCleanupChipViewModel(k, Localizer.Format(L.GpoCleanup.Chip, Localizer[L.GpoCleanup.KindTitleKey(k)], count), count, OnChipToggled);
            chip.Reflect(selected == k);
            return chip;
        })];
    }

    /// <summary>The kind options in the UI language (the selection is kept).</summary>
    private void BuildFilterOptions()
    {
        var kind = SelectedKind?.Value;
        _applyingFilters = true;
        try
        {
            KindOptions = [new GpoOption<GpoCleanupKind?>(null, Localizer[L.GpoCleanup.AllKinds]),
                .. Enum.GetValues<GpoCleanupKind>().Select(k => new GpoOption<GpoCleanupKind?>(k, Localizer[L.GpoCleanup.KindTitleKey(k)]))];
            OnPropertyChanged(nameof(KindOptions));
            SelectedKind = KindOptions.First(o => o.Value == kind);
            if (GpoOptions.Count > 0)
            {
                BuildGpoOptions();
            }
        }
        finally
        {
            _applyingFilters = false;
        }
    }

    /// <summary>"Każdy obiekt GPO" and the GPOs of the proposals by name (the selection is kept when the GPO still has proposals).</summary>
    private void BuildGpoOptions()
    {
        var gpo = SelectedGpo?.Value;
        var compare = StringComparer.Create(Localizer.Culture, ignoreCase: true);
        var applying = _applyingFilters;
        _applyingFilters = true;
        try
        {
            GpoOptions = [new GpoOption<Guid?>(null, Localizer[L.GpoCleanup.AllGpos]),
                .. _all.GroupBy(r => r.GpoId)
                    .Select(g => new GpoOption<Guid?>(g.Key, g.First().GpoName))
                    .OrderBy(o => o.Text, compare)];
            SelectedGpo = GpoOptions.FirstOrDefault(o => o.Value == gpo) ?? GpoOptions[0];
        }
        finally
        {
            _applyingFilters = applying;
        }
    }
}
