using System.Globalization;
using System.Text;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.Settings;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>A row of the "Ustawienia" tab: a setting, a preference item or an administrative template policy in GPMC's words.</summary>
public sealed class GpoSettingRowViewModel
{
    public GpoSettingRowViewModel(GpoSettingText text)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        CanEdit = GroupPolicyActions.CanEditSetting(text.Focus);
    }

    /// <summary>What the presenter made of the setting(s).</summary>
    public GpoSettingText Text { get; }

    /// <summary>"Konfiguracja komputera" (the first level of the list's groups).</summary>
    public string Configuration => Text.Configuration;

    /// <summary>"Zasady › Ustawienia systemu Windows › … › Zasady haseł" (the second level).</summary>
    public string Group => Text.Path;

    /// <summary>The whole path, configuration included.</summary>
    public string GroupPath => Text.GroupPath;

    public string Name => Text.Name;

    public string Value => Text.ValueText;

    /// <summary>The key behind the row (tool tip, report, search).</summary>
    public string Technical => Text.Technical;

    /// <summary>The settings file (relative path; empty when unknown).</summary>
    public string Source => Text.Source ?? string.Empty;

    public IReadOnlyList<string> Elements => Text.Elements;

    public bool HasElements => Text.Elements.Count > 0;

    public string? Explanation => Text.Explanation;

    public bool HasExplanation => !string.IsNullOrEmpty(Text.Explanation);

    /// <summary>An editor of AD Manager edits this setting ("Edytuj to ustawienie").</summary>
    public bool CanEdit { get; }

    /// <summary>"Name: value", the parts one per line and where GPMC lists it – what "Kopiuj" puts on the clipboard.</summary>
    public string CopyText
    {
        get
        {
            var text = new StringBuilder();
            text.Append(Name).Append(": ").Append(Value);
            foreach (var element in Elements.Where(e => !string.Equals(e, Value, StringComparison.Ordinal)))
            {
                text.AppendLine().Append("  ").Append(element);
            }

            text.AppendLine().Append(GroupPath);
            return text.ToString();
        }
    }

    /// <summary>The search box's text is in the name, value, parts, path or technical key (case ignored).</summary>
    public bool Matches(string search) =>
        search.Length == 0
        || Contains(Name, search) || Contains(Value, search) || Contains(Technical, search) || Contains(GroupPath, search)
        || Elements.Any(e => Contains(e, search)) || Contains(Explanation, search);

    /// <summary>The row's name and kind only (values stay out of logs).</summary>
    public override string ToString() => Text.ToString();

    private static bool Contains(string? text, string search) =>
        text is not null && CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, search, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
}

/// <summary>
/// The "Ustawienia" tab (K6, design note docs/dev/k-computers-and-gpo.md 20.1): the GPO's settings in the operator's
/// language (<see cref="GpoSettingPresenter"/>), grouped like GPMC's settings report, with a search box (Ctrl+F, Esc),
/// "Edytuj to ustawienie", "Kopiuj" and "Raport HTML…". SIDs are named once per load; the administrative templates are read
/// after the rows are shown (rows first name registry values, then their policies; a failure leaves the registry names and a
/// quiet note). Everything lives in memory; the report goes only to a file the operator picks (rule 1).
/// </summary>
public sealed partial class GroupPolicyObjectDocumentViewModel : ISearchableDocument
{
    private readonly SerialCancellation _settingsUpgrade = new();
    private IReadOnlyList<GpoSettingRowViewModel> _allSettings = [];
    private bool _searchFocusPending;

    /// <summary>Ctrl+F: the search box of the "Ustawienia" tab (<see cref="ISearchableDocument"/>).</summary>
    public event EventHandler? SearchFocusRequested;

    /// <summary>What the search box holds (name, value, technical key; an empty text shows every setting).</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditSettingCommand), nameof(CopySettingCommand))]
    public partial GpoSettingRowViewModel? SelectedSetting { get; set; }

    /// <summary>"Pokazano ustawienia: 3 z 12", "Żadne ustawienie nie pasuje do wyszukiwania." (empty without a search).</summary>
    [ObservableProperty]
    public partial string SettingsSummary { get; private set; } = string.Empty;

    /// <summary>A quiet note on the administrative templates (being read, not available); null when the policies are named.</summary>
    [ObservableProperty]
    public partial string? DefinitionsNote { get; private set; }

    /// <summary>Every row of the tab (the search shows <see cref="Settings"/>, a part of them).</summary>
    public int SettingCount => _allSettings.Count;

    /// <summary>The settings of the last load (nothing of the directory beyond this GPO's content).</summary>
    public IReadOnlyList<GpoSettingRowViewModel> AllSettings => _allSettings;

    /// <summary>Ctrl+F: shows the "Ustawienia" tab and moves the keyboard to its search box.</summary>
    public void FocusSearch()
    {
        _searchFocusPending = SelectedTab != GpoDocumentTab.Settings;
        SelectedTab = GpoDocumentTab.Settings;
        SearchFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// True once after <see cref="FocusSearch"/> switched to the "Ustawienia" tab: the search box, created only when the tab is
    /// shown, takes the keyboard when it appears.
    /// </summary>
    public bool TakeSearchFocusRequest()
    {
        var pending = _searchFocusPending;
        _searchFocusPending = false;
        return pending;
    }

    partial void OnSearchTextChanged(string value) => ApplySettingsFilter();

    /// <summary>"Edytuj to ustawienie": the editor of the selected setting (K3 at the policy, K4 at the page and row).</summary>
    [RelayCommand(CanExecute = nameof(CanEditSetting))]
    private void EditSetting()
    {
        if (SelectedSetting is { CanEdit: true, Text.Focus: { } focus })
        {
            Actions.EditSetting(GpoId, Title, focus);
        }
    }

    private bool CanEditSetting() => SelectedSetting is { CanEdit: true } && _gpo is not null && HasSession;

    /// <summary>"Kopiuj": the selected row as the tab shows it.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedSetting))]
    private void CopySetting()
    {
        if (SelectedSetting is { } row)
        {
            ClipboardCopy.Text(Gp.Ui.Clipboard, Gp.Ui.Notifications, row.CopyText, Localizer[L.GpoSettings.Copied]);
        }
    }

    private bool HasSelectedSetting() => SelectedSetting is not null;

    /// <summary>"Raport HTML…": every setting of the GPO as a self-contained HTML page in a file the operator chooses.</summary>
    [RelayCommand(CanExecute = nameof(HasSettings))]
    private async Task HtmlReportAsync()
    {
        var rows = _allSettings;
        var name = Title;
        var path = await Gp.Ui.Files.SaveFileAsync(Localizer[L.GpoSettings.HtmlReport], ReportFileName(name),
            [new FileFilter(Localizer[L.GpoSettings.HtmlFilter], "*.html")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var session = Gp.Sessions.Current;
        var meta = Localizer.Format(L.GpoSettings.ReportMeta, DomainText, GuidText, session?.Info.Server ?? "–",
            TimeZoneInfo.ConvertTime(Gp.Time.GetUtcNow(), TimeZoneInfo.Local).ToString("g", Localizer.Culture));
        var html = GpoSettingsReport.Build(Localizer, Localizer.Format(L.GpoSettings.ReportTitle, name), meta, [.. rows.Select(r => r.Text)]);
        try
        {
            await Task.Run(() => File.WriteAllTextAsync(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))).ConfigureAwait(true);
            Gp.Ui.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.GpoSettings.ReportExported, rows.Count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Gp.Ui.Notifications.Show(NotificationSeverity.Error, Localizer.Format(L.GpoSettings.ReportFailed, Gp.Errors.Translate(ex)));
        }
    }

    private bool HasSettings() => _allSettings.Count > 0;

    /// <summary>
    /// Shows the settings of a load: SIDs named once (one directory search per 50 SIDs), rows grouped and ordered as GPMC
    /// lists them; then – without holding the load – the administrative templates name the Registry.pol values' policies.
    /// </summary>
    private async Task ShowSettingsAsync(IDirectorySession session, GpoFolderContent? content, IReadOnlyList<GpoSetting> settings, string? note,
        CancellationToken cancellationToken)
    {
        var upgrade = _settingsUpgrade.Next();
        var names = settings.Count == 0
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : await TrusteeNames.ResolveAsync(session, GpoSettingPresenter.Sids(settings), Localizer, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        var itemNames = GpoSettingPresenter.ItemNamesOf(settings);
        var presenter = new GpoSettingPresenter(Localizer, names) { ItemNames = itemNames };
        ShowRows(presenter.DescribeRows(settings));
        SettingsNote = note ?? (settings.Count == 0 ? Localizer[L.GroupPolicy.NoSettings] : null);
        DefinitionsNote = null;

        var named = settings.Any(s => s.Kind != GpoSettingKind.Unreadable
            && (s.Key.Contains("/Registry/", StringComparison.OrdinalIgnoreCase) || s.Key.Contains("/Comment/", StringComparison.OrdinalIgnoreCase)));
        if (content is not null && named && Gp.Definitions is { } definitions)
        {
            NamePoliciesAsync(session, definitions, content, settings, names, itemNames, upgrade).Forget();
        }
    }

    /// <summary>The second step of a load: Registry.pol values become the administrative template policies that configure them.</summary>
    private async Task NamePoliciesAsync(IDirectorySession session, PolicyDefinitionsService definitions, GpoFolderContent content, IReadOnlyList<GpoSetting> settings,
        IReadOnlyDictionary<string, string> names, IReadOnlyDictionary<string, string> itemNames, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Gp.Lifetime);
        var token = linked.Token;
        DefinitionsNote = Localizer[L.GpoSettings.DefinitionsLoading];
        try
        {
            var access = await Gp.OpenSysvolAsync(session, token).ConfigureAwait(true);
            PolicyDefinitionsResult result;
            try
            {
                result = await definitions.GetAsync(session, access.Connection, Localizer.Culture, reload: false, token).ConfigureAwait(true);
            }
            finally
            {
                if (access.Connection is { } connection)
                {
                    await connection.DisposeAsync().ConfigureAwait(true);
                }
            }

            if (token.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            if (result.Set.PolicyCount == 0)
            {
                DefinitionsNote = Localizer[L.GpoSettings.DefinitionsNone];
                return;
            }

            var localizer = Localizer;
            var rows = await Task.Run(
                () =>
                {
                    var (computer, user) = GpoSettingPresenter.Policies(content, result.Set);
                    var presenter = new GpoSettingPresenter(localizer, names, result.Set) { ComputerPolicies = computer, UserPolicies = user, ItemNames = itemNames };
                    return presenter.DescribeRows(settings);
                },
                token).ConfigureAwait(true);
            if (token.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            ShowRows(rows);
            DefinitionsNote = null;
        }
        catch (OperationCanceledException)
        {
            // A newer load, or the window closed.
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex) || ex is GpoParseException or System.Xml.XmlException)
        {
            if (!token.IsCancellationRequested && !IsDisposed)
            {
                DefinitionsNote = Localizer[L.GpoSettings.DefinitionsFailed];
            }
        }
    }

    private void ShowRows(IReadOnlyList<GpoSettingText> texts)
    {
        var selected = SelectedSetting?.Text.Key;
        _allSettings = [.. GpoSettingPresenter.Ordered(texts, Localizer).Select(t => new GpoSettingRowViewModel(t))];
        OnPropertyChanged(nameof(SettingCount));
        OnPropertyChanged(nameof(AllSettings));
        ApplySettingsFilter();
        SelectedSetting = selected is null ? null : Settings.FirstOrDefault(r => r.Text.Keys.Contains(selected, StringComparer.OrdinalIgnoreCase));
        HtmlReportCommand.NotifyCanExecuteChanged();
        EditSettingCommand.NotifyCanExecuteChanged();
    }

    private void ApplySettingsFilter()
    {
        // A new list clears the view's selection: the selected row stays selected while the search still shows it.
        var selected = SelectedSetting;
        var search = SearchText.Trim();
        Settings = search.Length == 0 ? _allSettings : [.. _allSettings.Where(r => r.Matches(search))];
        SettingsSummary = search.Length == 0 || _allSettings.Count == 0 ? string.Empty
            : Settings.Count == 0 ? Localizer[L.GpoSettings.NoMatches]
            : Localizer.Format(L.GpoSettings.Shown, Settings.Count, _allSettings.Count);
        SelectedSetting = selected is not null && Settings.Contains(selected) ? selected : null;
    }

    private void ClearSettings()
    {
        _settingsUpgrade.Cancel();
        _allSettings = [];
        Settings = [];
        SelectedSetting = null;
        SettingsSummary = string.Empty;
        DefinitionsNote = null;
        OnPropertyChanged(nameof(SettingCount));
        OnPropertyChanged(nameof(AllSettings));
        HtmlReportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"Stacje robocze – Zasady haseł – ustawienia.html" (characters a file name cannot hold replaced).</summary>
    private static string ReportFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. name.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c)]).Trim();
        return (safe.Length == 0 ? "gpo" : safe) + ".html";
    }
}
