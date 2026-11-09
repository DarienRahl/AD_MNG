using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.Search;

/// <summary>The area facet: one area, or the preference items of one type (<see cref="PreferenceType"/>).</summary>
public sealed record GpoSearchAreaFilter(GpoSearchArea Area, string? PreferenceType = null)
{
    /// <summary>For diagnostics: the area and type.</summary>
    public override string ToString() => PreferenceType is null ? Area.ToString() : Area + "/" + PreferenceType;
}

/// <summary>The query, the facets, the debounced matching and the results of "Wyszukiwarka ustawień GPO".</summary>
public sealed partial class GpoSettingsSearchDocumentViewModel
{
    /// <summary>The pause after the last keystroke before the search runs.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly SerialCancellation _search = new();
    private bool _applyingOptions;
    private IReadOnlyList<GpoSearchRowViewModel> _selectedRows = [];

    /// <summary>The occurrence to select after the next search (GPO and unit id).</summary>
    private (Guid Gpo, string Unit)? _restoreSelection;

    /// <summary>Ctrl+F: the view focuses the search box.</summary>
    public event EventHandler? SearchFocusRequested;

    /// <summary>The query (case and Polish letters ignored; at least <see cref="GpoSettingsMatcher.MinLength"/> characters).</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    public IReadOnlyList<GpoOption<GpoSearchField>> FieldOptions { get; private set; }

    /// <summary>"Wszystko / Nazwa / Wartość / Klucz techniczny / Konto".</summary>
    [ObservableProperty]
    public partial GpoOption<GpoSearchField>? SelectedField { get; set; }

    public IReadOnlyList<GpoOption<GpoScope?>> ScopeOptions { get; private set; }

    [ObservableProperty]
    public partial GpoOption<GpoScope?>? SelectedScope { get; set; }

    /// <summary>Every area, the areas, and the preferences by type (the types of the index).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GpoOption<GpoSearchAreaFilter?>> AreaOptions { get; private set; }

    [ObservableProperty]
    public partial GpoOption<GpoSearchAreaFilter?>? SelectedArea { get; set; }

    /// <summary>"Tylko różne wartości".</summary>
    [ObservableProperty]
    public partial bool OnlyDifferent { get; set; }

    /// <summary>The groups of the last search (at most <see cref="GpoSettingsMatcher.MaxGroups"/>).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GpoSearchGroupViewModel> Groups { get; private set; } = [];

    /// <summary>The rows of <see cref="Groups"/> in order (the list groups them by <see cref="GpoSearchRowViewModel.Group"/>).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GpoSearchRowViewModel> Rows { get; private set; } = [];

    /// <summary>The row whose details are shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText), nameof(HasSelection))]
    public partial GpoSearchRowViewModel? SelectedRow { get; set; }

    public bool HasSelection => SelectedRow is not null;

    /// <summary>The rows selected in the list (the view keeps it; "Porównaj GPO" and "Kopiuj do innego GPO…" work on it).</summary>
    public IReadOnlyList<GpoSearchRowViewModel> SelectedRows
    {
        get => _selectedRows;
        set
        {
            _selectedRows = value ?? [];
            OnPropertyChanged();
            RefreshSelectionCommands();
        }
    }

    /// <summary>The query is too short: the summary is shown instead of results.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSummary))]
    public partial bool IsSummaryShown { get; private set; } = true;

    /// <summary>Settings were read and the query is too short: the view shows the summary over the (empty) list.</summary>
    public bool ShowsSummary => HasIndex && IsSummaryShown;

    /// <summary>The settings per area and in total (shown for an empty or one-character query).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> SummaryLines { get; private set; } = [];

    /// <summary>"Znalezione ustawienia: 3 (wystąpienia: 7)" / "Żadne ustawienie nie pasuje do wyszukiwania." (empty without a query).</summary>
    [ObservableProperty]
    public partial string ResultText { get; private set; } = string.Empty;

    /// <summary>"Zawęź wyszukiwanie – pokazano 2 000 z 2 345 grup" (null when everything is shown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCapped))]
    public partial string? CapNote { get; private set; }

    public bool IsCapped => CapNote is not null;

    /// <summary>The query that produced <see cref="Groups"/> (null before the first search).</summary>
    public GpoSearchQuery? LastQuery { get; private set; }

    /// <summary>A facet other than "everything" is set.</summary>
    public bool IsFiltered => SelectedScope?.Value is not null || SelectedArea?.Value is not null || OnlyDifferent;

    /// <summary>Ctrl+F (<see cref="Shell.ISearchableDocument"/>).</summary>
    public void FocusSearch() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Enter in the search box (and the tests): searches now, without the debounce.</summary>
    [RelayCommand(CanExecute = nameof(HasIndex))]
    public Task SearchAsync() => SearchCoreAsync(TimeSpan.Zero);

    /// <summary>Esc in the search box.</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>"Wyczyść filtry": both configurations, every area, every value (the field and the text stay).</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        _applyingOptions = true;
        try
        {
            SelectedScope = ScopeOptions[0];
            SelectedArea = AreaOptions[0];
            OnlyDifferent = false;
        }
        finally
        {
            _applyingOptions = false;
        }

        OptionsChanged();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_corpus is not null)
        {
            SearchCoreAsync(Debounce).Forget();
        }
    }

    partial void OnSelectedFieldChanged(GpoOption<GpoSearchField>? value) => OptionsChanged();

    partial void OnSelectedScopeChanged(GpoOption<GpoScope?>? value) => OptionsChanged();

    partial void OnSelectedAreaChanged(GpoOption<GpoSearchAreaFilter?>? value) => OptionsChanged();

    partial void OnOnlyDifferentChanged(bool value) => OptionsChanged();

    partial void OnSelectedRowChanged(GpoSearchRowViewModel? value)
    {
        OnPropertyChanged(nameof(StatusText));
        RefreshSelectionCommands();
    }

    private void OptionsChanged()
    {
        OnPropertyChanged(nameof(IsFiltered));
        if (!_applyingOptions && _corpus is not null)
        {
            SearchCoreAsync(TimeSpan.Zero).Forget();
        }
    }

    private void RefreshSelectionCommands()
    {
        OpenGpoCommand.NotifyCanExecuteChanged();
        EditSettingCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
        CopyToCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Runs the query after <paramref name="delay"/> (a newer request cancels it): the account resolved for "Konto", the
    /// matching on <c>Task.Run</c>, then the groups and rows. Never throws.
    /// </summary>
    private async Task SearchCoreAsync(TimeSpan delay)
    {
        var token = _search.Next();
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token).ConfigureAwait(true);
            }

            if (_corpus is not { } corpus)
            {
                return;
            }

            var query = await QueryAsync(corpus, token).ConfigureAwait(true);
            var localizer = Localizer;
            var (result, groups) = await Task.Run(() =>
            {
                var found = GpoSettingsMatcher.Match(corpus, query, token);
                return (found, found.Groups.Select(g => CreateGroup(localizer, g)).ToList());
            }, token).ConfigureAwait(true);
            if (!_search.IsCurrent(token) || !ReferenceEquals(corpus, _corpus) || IsDisposed)
            {
                return;
            }

            ShowResult(query, result, groups);
        }
        catch (OperationCanceledException)
        {
            // A newer query, the tab closed, or the index was replaced.
        }
    }

    /// <summary>The query of the box and the facets; "Konto" with the SIDs the text names (locally, else one LDAP lookup).</summary>
    private async Task<GpoSearchQuery> QueryAsync(GpoSearchCorpus corpus, CancellationToken cancellationToken)
    {
        var field = SelectedField?.Value ?? GpoSearchField.All;
        var area = SelectedArea?.Value;
        var query = new GpoSearchQuery(SearchText ?? string.Empty)
        {
            Field = field,
            Scope = SelectedScope?.Value,
            Area = area?.Area,
            PreferenceType = area?.PreferenceType,
            OnlyDifferent = OnlyDifferent,
        };
        if (field != GpoSearchField.Account || query.Text.Trim().Length == 0)
        {
            return query;
        }

        var text = query.Text.Trim();
        var sids = GpoSettingsMatcher.AccountSids(corpus.View, text);
        if (sids.Count == 0 && (text.Contains('\\', StringComparison.Ordinal) || text.Contains('@', StringComparison.Ordinal))
            && Gp.Sessions.Current is { } session)
        {
            try
            {
                var resolved = await GpoPrincipalResolver.ResolveAsync(session, [text], corpus.View.Index.DomainSid, cancellationToken).ConfigureAwait(true);
                sids = [.. resolved.Where(p => p.Sid is not null).Select(p => p.Sid!)];
            }
            catch (DirectoryException)
            {
                // Not found by LDAP: the names the index resolved still match by substring.
            }
        }

        return query with { AccountSids = sids };
    }

    /// <summary>A group with its rows, the GPOs by name (pure: runs on the matching thread).</summary>
    private static GpoSearchGroupViewModel CreateGroup(ILocalizer localizer, GpoSearchUnitGroup group)
    {
        var compare = StringComparer.Create(localizer.Culture, ignoreCase: true);
        return new GpoSearchGroupViewModel(localizer, group, g => [.. group.Occurrences
            .OrderBy(o => o.Entry.Gpo.DisplayName, compare)
            .ThenBy(o => o.Unit.Occurrence)
            .Select(o => new GpoSearchRowViewModel(localizer, g, o))]);
    }

    private void ShowResult(GpoSearchQuery query, GpoSearchResult result, IReadOnlyList<GpoSearchGroupViewModel> groups)
    {
        var restore = _restoreSelection ?? (SelectedRow is { } selected ? KeyOf(selected) : null);
        _restoreSelection = null;
        LastQuery = query;
        IsSummaryShown = result.IsSummary;
        Groups = groups;
        Rows = [.. groups.SelectMany(g => g.Rows)];
        SelectedRows = [];
        SelectedRow = restore is { } key ? Rows.FirstOrDefault(r => r.GpoId == key.Gpo && string.Equals(r.Unit.Id, key.Unit, StringComparison.OrdinalIgnoreCase)) : null;
        var culture = Localizer.Culture;
        CapNote = result.IsCapped
            ? Localizer.Format(L.GpoSearch.Capped, groups.Count.ToString("N0", culture), result.TotalGroups.ToString("N0", culture))
            : null;
        ResultText = result.IsSummary ? string.Empty
            : groups.Count == 0 ? Localizer[L.GpoSearch.NoResults]
            : Localizer.Format(L.GpoSearch.Results, result.TotalGroups.ToString("N0", culture), Rows.Count.ToString("N0", culture));
        LoadedText = result.IsSummary ? IndexInfoText : ResultText;
        OnPropertyChanged(nameof(StatusText));
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
    }

    private void ClearResults()
    {
        Groups = [];
        Rows = [];
        SelectedRows = [];
        SelectedRow = null;
        CapNote = null;
        ResultText = string.Empty;
        IsSummaryShown = true;
        SummaryLines = [];
        LastQuery = null;
    }

    private static (Guid Gpo, string Unit) KeyOf(GpoSearchRowViewModel row) => (row.GpoId, row.Unit.Id);

    /// <summary>The fields and configurations in the UI language (the selections are kept).</summary>
    private void BuildOptions()
    {
        var field = SelectedField?.Value ?? GpoSearchField.All;
        var scope = SelectedScope?.Value;
        _applyingOptions = true;
        try
        {
            FieldOptions = [.. Enum.GetValues<GpoSearchField>().Select(f => new GpoOption<GpoSearchField>(f, Localizer[L.GpoSearch.FieldKey(f)]))];
            ScopeOptions =
            [
                new GpoOption<GpoScope?>(null, Localizer[L.GpoSearch.AllScopes]),
                new GpoOption<GpoScope?>(GpoScope.Computer, Localizer[L.GpoSearch.ScopeComputer]),
                new GpoOption<GpoScope?>(GpoScope.User, Localizer[L.GpoSearch.ScopeUser]),
            ];
            OnPropertyChanged(nameof(FieldOptions));
            OnPropertyChanged(nameof(ScopeOptions));
            SelectedField = FieldOptions.First(o => o.Value == field);
            SelectedScope = ScopeOptions.First(o => o.Value == scope);
        }
        finally
        {
            _applyingOptions = false;
        }

        BuildAreaOptions();
    }

    /// <summary>Every area, the six areas and one entry per preference type of the index (the selection is kept when it still exists).</summary>
    private void BuildAreaOptions()
    {
        var area = SelectedArea?.Value;
        var preferences = Localizer[L.GpoSearch.AreaKey(GpoSearchArea.Preferences)];
        var options = new List<GpoOption<GpoSearchAreaFilter?>> { new(null, Localizer[L.GpoSearch.AllAreas]) };
        foreach (var value in Enum.GetValues<GpoSearchArea>())
        {
            options.Add(new GpoOption<GpoSearchAreaFilter?>(new GpoSearchAreaFilter(value), Localizer[L.GpoSearch.AreaKey(value)]));
            if (value == GpoSearchArea.Preferences)
            {
                foreach (var type in _corpus?.PreferenceTypes ?? [])
                {
                    var key = L.GpoSettings.PreferenceTypeKey(type);
                    options.Add(new GpoOption<GpoSearchAreaFilter?>(new GpoSearchAreaFilter(value, type),
                        Localizer.Format(L.GpoSearch.PreferenceArea, preferences, Localizer.Contains(key) ? Localizer[key] : type)));
                }
            }
        }

        _applyingOptions = true;
        try
        {
            AreaOptions = options;
            SelectedArea = options.FirstOrDefault(o => Equals(o.Value, area)) ?? options[0];
        }
        finally
        {
            _applyingOptions = false;
        }

        OnPropertyChanged(nameof(IsFiltered));
    }

    /// <summary>For tests and the export: the shown rows' GPO names by group (no directory access).</summary>
    internal IReadOnlyList<(string Setting, IReadOnlyList<string> Gpos)> Shown() =>
        [.. Groups.Select(g => (g.Name, (IReadOnlyList<string>)[.. g.Rows.Select(r => r.GpoName)]))];
}
