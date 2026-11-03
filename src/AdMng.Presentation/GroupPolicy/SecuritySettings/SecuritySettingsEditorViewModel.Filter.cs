using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.SecuritySettings;

/// <summary>The pages of the security settings editor, in the order of its tabs.</summary>
public enum SecuritySettingsPage
{
    /// <summary>"Grupy lokalne": Restricted Groups and GPP local groups.</summary>
    Groups,

    /// <summary>"Prawa użytkownika".</summary>
    Rights,

    /// <summary>"Zasady kont i opcje zabezpieczeń".</summary>
    Options,

    /// <summary>"Inspekcja".</summary>
    Audit,

    /// <summary>"Usługi".</summary>
    Services,
}

/// <summary>
/// Search and filters of every page (K6, design note 20.3): "Szukaj", "Tylko zdefiniowane", "Tylko zmienione" narrow the
/// lists of all five pages at once (names, constants, keys, values and members, case and diacritics ignored). With a search
/// or a filter the options list spans every group (with the group column). A row once listed stays listed while it is
/// edited – undefining a right under "Tylko zdefiniowane" does not pull it from under the mouse; changing the search or a
/// filter lists again.
/// </summary>
public sealed partial class SecuritySettingsEditorViewModel
{
    /// <summary>Rows listed since the search or a filter last changed (kept while they are edited).</summary>
    private readonly HashSet<object> _listed = new(ReferenceEqualityComparer.Instance);

    private IReadOnlyList<SecurityOptionRowViewModel> _options = [];

    /// <summary>Ctrl+F: the view focuses the search box.</summary>
    public event EventHandler? SearchFocusRequested;

    /// <summary>The page shown (the tab of the view).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPageIndex))]
    public partial SecuritySettingsPage SelectedPage { get; set; }

    /// <summary><see cref="SelectedPage"/> as the index of the view's tab.</summary>
    public int SelectedPageIndex
    {
        get => (int)SelectedPage;
        set => SelectedPage = Enum.IsDefined((SecuritySettingsPage)value) ? (SecuritySettingsPage)value : SecuritySettingsPage.Groups;
    }

    /// <summary>Search in names, constants, keys, values and members (case and diacritics ignored).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltering))]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>"Tylko zdefiniowane": settings the GPO defines.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltering))]
    public partial bool OnlyDefined { get; set; }

    /// <summary>"Tylko zmienione": settings changed and not saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltering))]
    public partial bool OnlyChanged { get; set; }

    /// <summary>A search or a filter narrows the pages.</summary>
    public bool IsFiltering => SearchText.Trim().Length > 0 || OnlyDefined || OnlyChanged;

    /// <summary>The options list spans every group: the group column is shown.</summary>
    public bool ShowsOptionGroupColumn => IsFiltering && !IsRawGroupSelected;

    /// <summary>The options listed: the selected group's, or (searching, filtering) the matching ones of every group.</summary>
    public IReadOnlyList<SecurityOptionRowViewModel> Options => _options;

    [ObservableProperty]
    public partial IReadOnlyList<UserRightRowViewModel> VisibleUserRights { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<RestrictedGroupRowViewModel> VisibleRestrictedGroups { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<GppGroupItemViewModel> VisibleGppGroups { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<RawTemplateEntryViewModel> VisibleRawEntries { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<AuditRowViewModel> VisibleLegacyAudit { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<AuditCategoryViewModel> VisibleAuditCategories { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ServiceRowViewModel> VisibleServices { get; private set; } = [];

    /// <summary>Ctrl+F (<see cref="Shell.ISearchableDocument"/>).</summary>
    public void FocusSearch() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Esc in the search box.</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    partial void OnSearchTextChanged(string value) => Relist();

    partial void OnOnlyDefinedChanged(bool value) => Relist();

    partial void OnOnlyChangedChanged(bool value) => Relist();

    /// <summary>The search or a filter changed: every page lists again from scratch.</summary>
    private void Relist()
    {
        _listed.Clear();
        ApplyFilters();
        OnPropertyChanged(nameof(ShowsOptionGroupColumn));
    }

    /// <summary>The lists of every page after a rebuild or a change of the search or a filter.</summary>
    private void ApplyFilters()
    {
        if (!IsFiltering)
        {
            _listed.Clear();
        }

        var compare = CultureInfo.CurrentCulture.CompareInfo;
        var search = SearchText.Trim();
        bool Text(params string?[] texts) => search.Length == 0
            || texts.Any(t => t is not null && compare.IndexOf(t, search, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);

        Set(VisibleUserRights, Filter(UserRights, r => (!OnlyDefined || r.IsDefined) && Text(r.Name, r.Constant, r.HoldersText)), v => VisibleUserRights = v);
        Set(VisibleRestrictedGroups, Filter(RestrictedGroups, r => Text(r.Group?.DisplayName, r.GroupValue, r.MembersText, r.MemberOfText)), v => VisibleRestrictedGroups = v);
        Set(VisibleGppGroups, Filter(GppGroups, r => Text(r.GroupName, r.GroupDetail, r.MembersText, r.SelectedAction?.Label)), v => VisibleGppGroups = v);
        Set(VisibleRawEntries, Filter(RawEntries, r => Text(r.Section, r.Key, r.Value)), v => VisibleRawEntries = v);
        Set(VisibleServices, Filter(Services, r => Text(r.Name, r.ModeText, r.SecurityText)), v => VisibleServices = v);

        bool Audit(AuditRowViewModel r) => (!OnlyDefined || r.SelectedState?.Value is not null) && Text(r.Name, r.Id, r.SelectedState?.Label);
        Set(VisibleLegacyAudit, Filter(LegacyAudit, Audit), v => VisibleLegacyAudit = v);
        var categories = IsFiltering
            ? [.. AdvancedAuditCategories.Select(c => (Category: c, Rows: Filter(c.Rows, Audit))).Where(c => c.Rows.Count > 0)
                .Select(c => c.Rows.Count == c.Category.Rows.Count ? c.Category : c.Category with { Rows = c.Rows })]
            : AdvancedAuditCategories;
        if (!categories.Select(c => c.Category).SequenceEqual(VisibleAuditCategories.Select(c => c.Category))
            || !categories.SelectMany(c => c.Rows).SequenceEqual(VisibleAuditCategories.SelectMany(c => c.Rows)))
        {
            VisibleAuditCategories = categories;
        }

        IReadOnlyList<SecurityOptionRowViewModel> options = IsFiltering
            ? Filter(_optionRows, r => (!OnlyDefined || r.IsDefined) && Text(r.Name, r.KeyText, r.ValueText, r.GroupName))
            : SelectedOptionGroup?.Rows ?? [];
        if (!options.SequenceEqual(_options))
        {
            _options = options;
            OnPropertyChanged(nameof(Options));
        }

        return;

        // A row matches, or was listed since the search or a filter changed; "Tylko zmienione" applies to every row.
        IReadOnlyList<T> Filter<T>(IReadOnlyList<T> rows, Func<T, bool> matches)
            where T : SecuritySettingRowBase
        {
            if (!IsFiltering)
            {
                return rows;
            }

            var result = new List<T>();
            foreach (var row in rows)
            {
                if (_listed.Contains(row) || ((!OnlyChanged || row.IsChanged) && matches(row)))
                {
                    result.Add(row);
                    _listed.Add(row);
                }
            }

            return result;
        }

        static void Set<T>(IReadOnlyList<T> current, IReadOnlyList<T> next, Action<IReadOnlyList<T>> assign)
        {
            if (!next.SequenceEqual(current))
            {
                assign(next);
            }
        }
    }
}
