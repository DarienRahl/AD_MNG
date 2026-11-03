using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// "Szablony administracyjne" of one GPO (K3, design note docs/dev/k-computers-and-gpo.md 6): the computer and user
/// configuration as a tree of the ADMX categories (central store of the session's DC, else the templates of this computer),
/// the policies of the selected category with their state, search over names, help texts and registry values, "tylko
/// skonfigurowane" / "tylko zmienione", the selected policy's editor, the Registry.pol entries no template describes, and
/// "Zapisz zmiany…" – one plan (Registry.pol, extension names, version) through the change plan preview (CLAUDE.md rule
/// 5). Edits live in memory until saved; a reload never drops them: a half whose file changed on SYSVOL meanwhile keeps the
/// edits and the conflict note (the save is refused as a conflict), a half saved as edited becomes clean, a half saved and
/// edited again keeps the later edits on top of what was saved. The selected policy can be edited only while the document
/// is neither loading nor saving; invalid options block the save; closing the tab with unsaved edits asks first.
/// K6 (20.3): the configured counter follows the edits; leaving a policy whose options are invalid keeps what was typed
/// (with a warning and a mark on its row) until it is corrected; Ctrl+S saves, Ctrl+F focuses the search; "Edytuj to
/// ustawienie" selects a policy (<see cref="FocusSetting"/>); the save shows "Zmiany ustawień" in the preview.
/// </summary>
public sealed partial class AdministrativeTemplatesEditorViewModel : GroupPolicyDocumentBase, ISaveableDocument, ISearchableDocument, IGroupPolicySettingEditor
{
    private readonly PolicyDefinitionsService _definitions;
    private readonly string _initialName;
    private readonly Dictionary<(PolicyClass, string), PolicyRowViewModel> _rows = [];
    private readonly HashSet<(PolicyClass Scope, string PolicyId)> _touched = [];

    /// <summary>What the last save asked to write per half: a reload that finds it on SYSVOL knows the save landed.</summary>
    private readonly Dictionary<PolicyClass, byte[]> _saved = [];
    private RegistryHalf? _machine;
    private RegistryHalf? _user;
    private PolicyDefinitionSet _set = PolicyDefinitionSet.Empty;
    private IReadOnlyDictionary<string, PolicyFileReadProblem> _fileProblems = new Dictionary<string, PolicyFileReadProblem>();
    private PolicyOwnershipIndex _ownership = PolicyOwnershipIndex.Empty;
    private GpoInfo? _gpo;
    private bool _confirmingClose;

    /// <summary>Editors left while their options were invalid: what was typed stays until it is corrected (never lost silently).</summary>
    private readonly Dictionary<(PolicyClass Scope, string PolicyId), PolicyEditorViewModel> _invalid = [];

    /// <summary>Configured policies of both halves (the counter of the status bar, kept up to date by the edits).</summary>
    private int _configured;

    /// <summary>"Edytuj to ustawienie" asked before the templates were loaded: applied when the load ends.</summary>
    private GroupPolicySettingFocus? _pendingFocus;

    /// <summary>The shown editor is being dropped with its typed options (discard): it is not kept.</summary>
    private bool _droppingEditor;

    public AdministrativeTemplatesEditorViewModel(GroupPolicyServices services, GroupPolicyActions actions, PolicyDefinitionsService definitions, Guid gpoId, string? name)
        : base(services, actions)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        GpoId = gpoId;
        _initialName = name ?? GroupPolicyText.Guid(gpoId);
    }

    public Guid GpoId { get; }

    /// <summary>Opens "Zaktualizuj szablony ADMX" (set by the factory; null: not offered).</summary>
    public Func<Task<bool>>? UpdateTemplates { get; init; }

    public override string Title => Localizer.Format(L.AdministrativeTemplates.Title, GpoName);

    /// <summary>The GPO's name (as read; the requested name until then).</summary>
    public string GpoName => _gpo?.DisplayName ?? _initialName;

    /// <summary>Ctrl+F: the view focuses the search box.</summary>
    public event EventHandler? SearchFocusRequested;

    public override ObjectIcon Icon => ObjectIcon.GroupPolicy;

    public override string SelectionText => ChangedCount > 0 ? Localizer.Format(L.AdministrativeTemplates.ChangedCount, ChangedCount) : string.Empty;

    // ---- Tree, list and editor -----------------------------------------------------------------------------------------

    /// <summary>"Konfiguracja komputera" and "Konfiguracja użytkownika".</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PolicyTreeNodeViewModel> Roots { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExtraSelected), nameof(ShowsCategoryColumn))]
    public partial PolicyTreeNodeViewModel? SelectedNode { get; set; }

    /// <summary>The policies of the selected node (with the search and the filters).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListNote))]
    public partial IReadOnlyList<PolicyRowViewModel> Policies { get; private set; } = [];

    [ObservableProperty]
    public partial PolicyRowViewModel? SelectedPolicy { get; set; }

    /// <summary>The selected policy's editor (null: none selected).</summary>
    [ObservableProperty]
    public partial PolicyEditorViewModel? Editor { get; private set; }

    /// <summary>Search in names, help texts and registry keys/values (diacritics and case ignored).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCategoryColumn))]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>"Tylko skonfigurowane": configured policies of the node and every category below it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCategoryColumn))]
    public partial bool OnlyConfigured { get; set; }

    /// <summary>"Tylko zmienione": policies changed and not saved yet, below the node.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCategoryColumn))]
    public partial bool OnlyChanged { get; set; }

    /// <summary>The list spans categories (search, filters, "Wszystkie ustawienia"): the category column is shown.</summary>
    public bool ShowsCategoryColumn => SearchText.Trim().Length > 0 || OnlyConfigured || OnlyChanged
        || SelectedNode?.Kind is PolicyTreeNodeKind.Scope or PolicyTreeNodeKind.AllSettings;

    /// <summary>"Brak pasujących ustawień." / "Wybierz kategorię." (null when the list has rows).</summary>
    public string? ListNote => IsExtraSelected || Policies.Count > 0 ? null
        : SelectedNode is null ? Localizer[L.AdministrativeTemplates.SelectCategory]
        : Localizer[L.AdministrativeTemplates.NoMatches];

    /// <summary>"Ustawienia rejestru spoza szablonów" is selected (the extra entries list is shown instead of policies).</summary>
    public bool IsExtraSelected => SelectedNode?.Kind == PolicyTreeNodeKind.Extra;

    [ObservableProperty]
    public partial IReadOnlyList<ExtraRegistryEntryViewModel> ExtraEntries { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteExtraEntryCommand))]
    public partial ExtraRegistryEntryViewModel? SelectedExtraEntry { get; set; }

    // ---- Notes ---------------------------------------------------------------------------------------------------------

    /// <summary>"Szablony: magazyn centralny na dc1 (plików: 213)" / local store / none.</summary>
    [ObservableProperty]
    public partial string? DefinitionsNote { get; private set; }

    /// <summary>No central store: the note is shown as a warning.</summary>
    [ObservableProperty]
    public partial bool IsCentralStoreMissing { get; private set; }

    /// <summary>Problems of the template files (localized).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDefinitionIssues), nameof(DefinitionIssuesTitle))]
    public partial IReadOnlyList<string> DefinitionIssues { get; private set; } = [];

    public bool HasDefinitionIssues => DefinitionIssues.Count > 0;

    public string DefinitionIssuesTitle => Localizer.Format(L.AdministrativeTemplates.DefinitionIssues, DefinitionIssues.Count);

    /// <summary>A Registry.pol that cannot be read (that half is read-only), else null.</summary>
    [ObservableProperty]
    public partial string? FileNote { get; private set; }

    /// <summary>The GPO's files changed on SYSVOL since the edits started (a save would be a conflict), else null.</summary>
    [ObservableProperty]
    public partial string? ConflictNote { get; private set; }

    /// <summary>Policies (and extra entries) changed and not saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges), nameof(SelectionText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    public partial int ChangedCount { get; private set; }

    /// <summary>A Registry.pol differs from what was read.</summary>
    public bool HasChanges => (_machine?.IsChanged ?? false) || (_user?.IsChanged ?? false);

    /// <summary>"Zapisz zmiany…" is running (plan, preview, execution): the editor is read-only meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand), nameof(DeleteExtraEntryCommand))]
    public partial bool IsSaving { get; private set; }

    /// <summary>The GPO can be edited now (connected, SYSVOL writable, loaded, not being saved).</summary>
    public bool CanEdit => IsSysvolWritable && _gpo is not null && !IsSaving;

    /// <summary>Closing the tab would discard edits (the shell asks first).</summary>
    public override bool HasUnsavedChanges => HasChanges;

    // ---- Commands ------------------------------------------------------------------------------------------------------

    /// <summary>"Zapisz zmiany…": one plan through the preview; the reload afterwards keeps what was not saved.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_gpo is not { } gpo)
        {
            return;
        }

        if (FirstInvalid() is { } invalid)
        {
            // The document holds the last valid options, not what the editor shows: never save something else than shown.
            Gp.Ui.Notifications.Show(NotificationSeverity.Warning, Localizer.Format(L.AdministrativeTemplates.SaveBlocked, invalid.Name));
            return;
        }

        var machine = _machine is { IsChanged: true, Error: null } m
            ? new RegistryPolicySave { Original = m.Original, Content = m.Document.ToBytes(), ClientExtensions = ClientExtensions(m) }
            : null;
        var user = _user is { IsChanged: true, Error: null } u
            ? new RegistryPolicySave { Original = u.Original, Content = u.Document.ToBytes(), ClientExtensions = ClientExtensions(u) }
            : null;
        if (machine is null && user is null)
        {
            return;
        }

        var changes = SettingChanges(machine is not null, user is not null);
        Remember(PolicyClass.Machine, machine);
        Remember(PolicyClass.User, user);
        IsSaving = true;
        SyncEditor();
        try
        {
            await Actions.SaveAdministrativeTemplatesAsync(gpo, machine, user, changes).ConfigureAwait(true);
        }
        finally
        {
            IsSaving = false;
            SyncEditor();
        }
    }

    /// <summary>"Odrzuć zmiany": the documents as read from SYSVOL again (then a reload shows the current files).</summary>
    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private async Task DiscardAsync()
    {
        if (!await Gp.Ui.Dialogs.ConfirmAsync(Localizer[L.AdministrativeTemplates.DiscardTitle], Localizer.Format(L.AdministrativeTemplates.DiscardMessage, ChangedCount),
            destructive: true).ConfigureAwait(true))
        {
            return;
        }

        _machine = _machine?.Discarded();
        _user = _user?.Discarded();
        _touched.Clear();
        _droppingEditor = true;
        try
        {
            Editor = null; // typed options are discarded with the rest: the reload shows the policy as read
        }
        finally
        {
            _droppingEditor = false;
        }

        ForgetInvalid();

        ConflictNote = null;
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// "Wczytaj szablony ponownie": the central store (or local store) is read again – the window's cache is dropped first,
    /// so a load superseded meanwhile (F5, a change elsewhere) still reads the store.
    /// </summary>
    [RelayCommand]
    private Task ReloadDefinitionsAsync()
    {
        _definitions.Invalidate();
        return LoadAsync();
    }

    /// <summary>"Zaktualizuj szablony…": the update window; the templates are read again when the store changed.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateTemplates))]
    private async Task UpdateTemplatesAsync()
    {
        if (UpdateTemplates is { } update && await update().ConfigureAwait(true))
        {
            _definitions.Invalidate();
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Closing the tab (Ctrl+W, its close button, the connection window) with unsaved edits: asks before they are
    /// discarded. True when the tab may close; never while its save is being planned.
    /// </summary>
    public override async Task<bool> CanCloseAsync()
    {
        if (!HasChanges || IsDisposed)
        {
            return true;
        }

        if (_confirmingClose || IsSaving)
        {
            // A second Ctrl+W while the question is open, or the save of these edits runs (its preview follows).
            return false;
        }

        _confirmingClose = true;
        try
        {
            return await Gp.Ui.Dialogs.ConfirmAsync(Title, Localizer.Format(L.AdministrativeTemplates.CloseDiscard, ChangedCount), destructive: true)
                .ConfigureAwait(true);
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    private bool CanUpdateTemplates() => UpdateTemplates is not null && IsSysvolWritable;

    /// <summary>Removes an entry no template describes (written on save).</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteExtraEntry))]
    private void DeleteExtraEntry()
    {
        if (!CanEdit || SelectedExtraEntry is not { } entry || Half(entry.Scope) is not { Error: null } half)
        {
            return;
        }

        half.Document.Remove(entry.Entry);
        _touched.Add((entry.Scope, "extra:" + entry.Entry.Key + "\n" + entry.Entry.ValueName));
        RebuildExtra();
        UpdateChanges();
    }

    /// <summary>Something to save, the GPO writable, and every typed option valid (an invalid value blocks the save).</summary>
    private bool CanSave() => HasChanges && CanEdit && !IsLoading && FirstInvalid() is null;

    /// <summary>Edits to drop: changed documents, or options typed invalid and kept.</summary>
    private bool CanDiscard() => (HasChanges || _invalid.Count > 0) && !IsSaving;

    /// <summary>The shown or a kept editor whose options are invalid (null: none).</summary>
    private PolicyEditorViewModel? FirstInvalid() =>
        Editor is { HasProblems: true } shown ? shown : _invalid.Values.FirstOrDefault(e => e.HasProblems);

    /// <summary>Ctrl+F (<see cref="ISearchableDocument"/>).</summary>
    public void FocusSearch() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Esc in the search box.</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private bool CanDeleteExtraEntry() => SelectedExtraEntry is not null && CanEdit && Half(SelectedExtraEntry.Scope) is { Error: null };

    // ---- Loading -------------------------------------------------------------------------------------------------------

    protected override async Task LoadCoreAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, CancellationToken cancellationToken)
    {
        var reader = Gp.Reader;
        var gpo = await Task.Run(() => reader.ReadPolicyAsync(session, GpoId, cancellationToken), cancellationToken).ConfigureAwait(true);
        if (gpo is null)
        {
            _gpo = null;
            Error = Localizer[L.GroupPolicy.Missing];
            LoadedText = Error;
            OnPropertyChanged(nameof(Title));
            return;
        }

        var definitions = await _definitions.GetAsync(session, sysvol, Localizer.Culture, reload: false, cancellationToken).ConfigureAwait(true);
        (byte[]? Content, string? Error) machineFile = (null, null), userFile = (null, null);
        if (sysvol is not null)
        {
            machineFile = await ReadFileAsync(sysvol, GroupPolicyPlanner.MachineRegistryPolicyPath, cancellationToken).ConfigureAwait(true);
            userFile = await ReadFileAsync(sysvol, GroupPolicyPlanner.UserRegistryPolicyPath, cancellationToken).ConfigureAwait(true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _gpo = gpo;
        OnPropertyChanged(nameof(Title));
        _machine = Reconcile(_machine, PolicyClass.Machine, sysvol is null ? null : machineFile);
        _user = Reconcile(_user, PolicyClass.User, sysvol is null ? null : userFile);
        ConflictNote = (_machine?.Conflict ?? false) || (_user?.Conflict ?? false) ? Localizer.Format(L.AdministrativeTemplates.ConflictNote, session.Info.Server) : null;
        FileNote = string.Join(Environment.NewLine, new[] { _machine?.Error, _user?.Error }.OfType<string>()) is { Length: > 0 } note ? note : null;

        var definitionsChanged = !ReferenceEquals(definitions.Set, _set);
        _set = definitions.Set;
        DefinitionsNote = definitions.Origin switch
        {
            PolicyDefinitionOrigin.CentralStore => Localizer.Format(L.AdministrativeTemplates.SourceCentral, definitions.Location, definitions.FileCount),
            PolicyDefinitionOrigin.LocalStore when sysvol is null => Localizer.Format(L.AdministrativeTemplates.SourceLocalWithoutSysvol, session.Info.Server, definitions.Location, definitions.FileCount),
            PolicyDefinitionOrigin.LocalStore => Localizer.Format(L.AdministrativeTemplates.SourceLocal, session.Info.Server, definitions.Location, definitions.FileCount),
            _ => Localizer.Format(L.AdministrativeTemplates.SourceNone, session.Info.Server),
        };
        IsCentralStoreMissing = definitions.Origin != PolicyDefinitionOrigin.CentralStore;
        _fileProblems = definitions.FileProblems;
        DefinitionIssues = [.. _set.Issues.Select(IssueText)];
        foreach (var stale in _invalid.Keys.Where(k => _set.Find(k.PolicyId) is null).ToList())
        {
            _invalid.Remove(stale); // its template is gone (another language, another store): nothing to correct
        }

        if (definitionsChanged || Roots.Count == 0)
        {
            _ownership = PolicyOwnershipIndex.Build(_set);
            BuildTree();
        }

        RefreshRows();
        RebuildList();
        RebuildExtra();
        UpdateChanges();
        ShowEditor();
        _configured = CountConfigured();
        UpdateLoadedText();
        if (_pendingFocus is { } focus)
        {
            _pendingFocus = null;
            FocusSetting(focus);
        }
    }

    protected override void OnNotConnected()
    {
        Roots = [];
        Policies = [];
        Editor = null;
        ExtraEntries = [];
    }

    protected override void RefreshCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        DeleteExtraEntryCommand.NotifyCanExecuteChanged();
        UpdateTemplatesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEdit));

        // The editor of the selected policy was built while loading (read-only then): editable again now.
        SyncEditor();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Editor is { } editor)
        {
            editor.PropertyChanged -= OnEditorPropertyChanged;
        }

        if (disposing)
        {
            ForgetInvalid();
        }

        base.Dispose(disposing);
    }

    protected override void OnCultureChanged()
    {
        // The texts come from the ADML of the new language: the templates are read again (the edits stay).
        _set = PolicyDefinitionSet.Empty;
        base.OnCultureChanged();
    }

    /// <summary>A Registry.pol of the GPO: its content (null: none) or a localized reason it cannot be read.</summary>
    private async Task<(byte[]? Content, string? Error)> ReadFileAsync(ISysvolConnection sysvol, string path, CancellationToken cancellationToken)
    {
        var result = await sysvol.ReadGpoFileAsync(GpoId, path, GpoTransaction.MaxFileBytes, cancellationToken).ConfigureAwait(true);
        return result.Status switch
        {
            SysvolFileStatus.Read or SysvolFileStatus.NotFound => (result.Content, null),
            SysvolFileStatus.AccessDenied => (null, Localizer[L.AdministrativeTemplates.FileAccessDenied]),
            SysvolFileStatus.TooLarge => (null, Localizer[L.AdministrativeTemplates.FileTooLarge]),
            _ => (null, Localizer[L.AdministrativeTemplates.FileReadFailed]),
        };
    }

    /// <summary>
    /// The half after a read of <paramref name="file"/> (null: SYSVOL unavailable – nothing to edit): without edits it is
    /// the file; with edits it stays edited unless the file now equals the edits (they were saved); a file that holds what
    /// the last save of this tab wrote rebases the edits made since onto it (no conflict: that change is ours); any other
    /// file that changed under the edits marks a conflict and keeps the old original (the save's compare-and-swap refuses it).
    /// </summary>
    private RegistryHalf? Reconcile(RegistryHalf? half, PolicyClass scope, (byte[]? Content, string? Error)? file)
    {
        if (file is not { } read)
        {
            return half is { IsChanged: true } ? half : null;
        }

        var path = scope == PolicyClass.Machine ? GroupPolicyPlanner.MachineRegistryPolicyPath : GroupPolicyPlanner.UserRegistryPolicyPath;
        if (read.Error is not null)
        {
            return half is { IsChanged: true } ? half : RegistryHalf.Unreadable(scope, Localizer.Format(L.AdministrativeTemplates.FileUnreadable, path, read.Error));
        }

        if (half is { IsChanged: true })
        {
            if (RegistryHalf.SameContent(read.Content, half.Document.ToBytes()))
            {
                ForgetTouched(scope);
                return RegistryHalf.Of(scope, read.Content);
            }

            if (RegistryHalf.SameContent(read.Content, half.Original))
            {
                return half;
            }

            if (_saved.TryGetValue(scope, out var saved) && RegistryHalf.SameContent(read.Content, saved))
            {
                // Our save landed, and the editor holds edits made after it (e.g. after a reload that could not read the
                // file): those edits stay, on top of the saved file.
                return half.Rebased(read.Content);
            }

            return half with { Conflict = true };
        }

        try
        {
            ForgetTouched(scope);
            return RegistryHalf.Of(scope, read.Content);
        }
        catch (GpoParseException)
        {
            return RegistryHalf.Unreadable(scope, Localizer.Format(L.AdministrativeTemplates.FileUnreadable, path, Localizer[L.AdministrativeTemplates.FileMalformed]));
        }
    }

    // ---- Tree and list -------------------------------------------------------------------------------------------------

    private void BuildTree()
    {
        var selected = SelectedNode;
        Roots = [ScopeNode(PolicyClass.Machine), ScopeNode(PolicyClass.User)];
        _rows.Clear();
        SelectedNode = Find(Roots, selected) ?? Roots[0];
        Roots[0].IsExpanded = true;
        Roots[1].IsExpanded = true;
    }

    private PolicyTreeNodeViewModel ScopeNode(PolicyClass scope)
    {
        var children = new List<PolicyTreeNodeViewModel>();
        children.AddRange(_set.Roots.Where(c => c.HasPolicies(scope)).Select(c => CategoryNode(c, scope)));
        if (_set.Uncategorized.Any(p => p.Policy.AppliesTo(scope)))
        {
            children.Add(new PolicyTreeNodeViewModel(PolicyTreeNodeKind.Uncategorized, scope, Localizer[L.AdministrativeTemplates.Uncategorized]));
        }

        children.Add(new PolicyTreeNodeViewModel(PolicyTreeNodeKind.AllSettings, scope, Localizer[L.AdministrativeTemplates.AllSettings]));
        children.Add(new PolicyTreeNodeViewModel(PolicyTreeNodeKind.Extra, scope, Localizer.Format(L.AdministrativeTemplates.ExtraSettings, 0)));
        return new PolicyTreeNodeViewModel(PolicyTreeNodeKind.Scope, scope,
            Localizer[scope == PolicyClass.Machine ? L.AdministrativeTemplates.ComputerConfiguration : L.AdministrativeTemplates.UserConfiguration], null, children);
    }

    private static PolicyTreeNodeViewModel CategoryNode(PolicyCategoryNode category, PolicyClass scope) =>
        new(PolicyTreeNodeKind.Category, scope, category.DisplayName, category,
            [.. category.Children.Where(c => c.HasPolicies(scope)).Select(c => CategoryNode(c, scope))]);

    private static PolicyTreeNodeViewModel? Find(IEnumerable<PolicyTreeNodeViewModel> nodes, PolicyTreeNodeViewModel? old)
    {
        if (old is null)
        {
            return null;
        }

        foreach (var node in nodes)
        {
            if (node.Kind == old.Kind && node.Scope == old.Scope && string.Equals(node.Category?.Id, old.Category?.Id, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            if (Find(node.Children, old) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    partial void OnSelectedNodeChanged(PolicyTreeNodeViewModel? value)
    {
        RebuildList();
        RebuildExtra();
    }

    partial void OnSearchTextChanged(string value) => RebuildList();

    partial void OnOnlyConfiguredChanged(bool value) => RebuildList();

    partial void OnOnlyChangedChanged(bool value) => RebuildList();

    partial void OnSelectedPolicyChanged(PolicyRowViewModel? value) => ShowEditor();

    /// <summary>The rows of the selected node (direct policies; with a search or filter every category below it).</summary>
    private void RebuildList()
    {
        if (SelectedNode is not { } node || node.Kind == PolicyTreeNodeKind.Extra)
        {
            Policies = [];
            SelectedPolicy = null;
            return;
        }

        var search = SearchText.Trim();
        var deep = search.Length > 0 || OnlyConfigured || OnlyChanged || node.Kind is PolicyTreeNodeKind.Scope or PolicyTreeNodeKind.AllSettings;
        IEnumerable<PolicyDefinition> candidates = node.Kind switch
        {
            PolicyTreeNodeKind.Category when deep => Below(node.Category!),
            PolicyTreeNodeKind.Category => node.Category!.Policies,
            PolicyTreeNodeKind.Uncategorized => _set.Uncategorized,
            _ => _set.Policies,
        };
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        var rows = new List<PolicyRowViewModel>();
        foreach (var definition in candidates.Where(d => d.Policy.AppliesTo(node.Scope)))
        {
            if (search.Length > 0 && !Matches(compare, definition, search))
            {
                continue;
            }

            var row = Row(definition, node.Scope);
            if ((OnlyConfigured && row.State == PolicyState.NotConfigured) || (OnlyChanged && !row.IsChanged && !row.HasProblem))
            {
                continue;
            }

            rows.Add(row);
        }

        var selected = SelectedPolicy;
        Policies = deep ? [.. rows.OrderBy(r => r.Name, StringComparer.Create(Localizer.Culture, ignoreCase: true))] : rows;
        SelectedPolicy = selected is not null && Policies.Contains(selected) ? selected : null;
    }

    private static IEnumerable<PolicyDefinition> Below(PolicyCategoryNode category) =>
        category.Policies.Concat(category.Children.SelectMany(Below));

    private static bool Matches(CompareInfo compare, PolicyDefinition definition, string search)
    {
        const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        var policy = definition.Policy;
        return compare.IndexOf(definition.DisplayName, search, Options) >= 0
            || compare.IndexOf(definition.ExplainText, search, Options) >= 0
            || compare.IndexOf(policy.Key, search, Options) >= 0
            || (policy.ValueName is { } name && compare.IndexOf(name, search, Options) >= 0)
            || policy.Elements.Any(e => (e.ValueName is { } elementName && compare.IndexOf(elementName, search, Options) >= 0)
                || (e.Key is { } key && compare.IndexOf(key, search, Options) >= 0));
    }

    /// <summary>The row of a policy (kept per scope, so the state of every listed row is refreshed in place).</summary>
    private PolicyRowViewModel Row(PolicyDefinition definition, PolicyClass scope)
    {
        if (!_rows.TryGetValue((scope, definition.Id), out var row))
        {
            row = new PolicyRowViewModel(definition, scope, definition.Category is { } category ? string.Join(" / ", category.PathNames()) : string.Empty);
            _rows[(scope, definition.Id)] = row;
            UpdateRow(row);
        }

        return row;
    }

    private void RefreshRows()
    {
        foreach (var row in _rows.Values)
        {
            UpdateRow(row);
        }
    }

    private void UpdateRow(PolicyRowViewModel row)
    {
        var half = Half(row.Scope);
        var state = half is null ? PolicySetting.NotConfigured : PolicyEngine.Read(row.Definition.Policy, half.Document.View, _ownership.OwnedNames);
        row.State = state.State;
        row.StateText = half is null || half.Error is not null ? Localizer[L.AdministrativeTemplates.StateUnknown] : StateText(state.State);
        row.IsChanged = half is not null && _touched.Contains((row.Scope, row.Definition.Id))
            && !Equals(PolicyEngine.Read(row.Definition.Policy, half.OriginalDocument.View, _ownership.OwnedNames), state);
        row.HasProblem = HasProblem(row.Scope, row.Definition.Id);
    }

    private string StateText(PolicyState state) => Localizer[state switch
    {
        PolicyState.Enabled => L.AdministrativeTemplates.StateEnabled,
        PolicyState.Disabled => L.AdministrativeTemplates.StateDisabled,
        PolicyState.Inconsistent => L.AdministrativeTemplates.StateInconsistent,
        _ => L.AdministrativeTemplates.StateNotConfigured,
    }];

    private void ShowEditor()
    {
        if (SelectedPolicy is not { } row)
        {
            Editor = null;
            return;
        }

        if (Editor is { HasProblems: true } shown && IsEditorOf(shown, row.Scope, row.Definition.Id))
        {
            // The same policy again (a reload, the list rebuilt): what was typed stays.
            shown.SetCanEdit(CanEditHalf(row.Scope));
            return;
        }

        if (_invalid.Remove((row.Scope, row.Definition.Id), out var kept))
        {
            // Back to a policy left with invalid options: the typed values are shown again, to be corrected.
            kept.SetCanEdit(CanEditHalf(row.Scope));
            Editor = kept;
            DiscardCommand.NotifyCanExecuteChanged();
            return;
        }

        var half = Half(row.Scope);
        var current = half is null ? PolicySetting.NotConfigured : PolicyEngine.Read(row.Definition.Policy, half.Document.View, _ownership.OwnedNames);
        Editor = new PolicyEditorViewModel(Localizer, row.Definition, row.Scope, current, CanEditHalf(row.Scope), Apply);
    }

    private static bool IsEditorOf(PolicyEditorViewModel editor, PolicyClass scope, string policyId) =>
        editor.Scope == scope && string.Equals(editor.Definition.Id, policyId, StringComparison.Ordinal);

    /// <summary>
    /// An editor left while its options are invalid: the document still holds the last valid setting, the typed options stay
    /// in the editor (shown again when the policy is selected), the row is marked and the save stays blocked; the operator is
    /// told – never a silent loss of what was typed.
    /// </summary>
    private void Keep(PolicyEditorViewModel editor)
    {
        _invalid[(editor.Scope, editor.Definition.Id)] = editor;
        MarkProblem(editor.Scope, editor.Definition.Id);
        Gp.Ui.Notifications.Show(NotificationSeverity.Warning, Localizer.Format(L.GroupPolicyEditing.InvalidKept, editor.Name));
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Drops the kept invalid options (discard, close).</summary>
    private void ForgetInvalid()
    {
        var kept = _invalid.Keys.ToList();
        _invalid.Clear();
        foreach (var (scope, id) in kept)
        {
            MarkProblem(scope, id);
        }
    }

    /// <summary>The row's warning mark: its options are invalid (shown or kept), not in the GPO.</summary>
    private void MarkProblem(PolicyClass scope, string policyId)
    {
        if (_rows.TryGetValue((scope, policyId), out var row))
        {
            row.HasProblem = HasProblem(scope, policyId);
        }
    }

    private bool HasProblem(PolicyClass scope, string policyId) =>
        (_invalid.TryGetValue((scope, policyId), out var kept) && kept.HasProblems)
        || (Editor is { HasProblems: true } shown && IsEditorOf(shown, scope, policyId));

    /// <summary>The selected policy's editor follows whether the GPO can be edited now (after a load, around a save).</summary>
    private void SyncEditor()
    {
        if (Editor is { } editor)
        {
            editor.SetCanEdit(CanEditHalf(editor.Scope));
        }
    }

    private bool CanEditHalf(PolicyClass scope) => CanEdit && Half(scope) is { Error: null };

    partial void OnEditorChanged(PolicyEditorViewModel? oldValue, PolicyEditorViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnEditorPropertyChanged;
            if (oldValue.HasProblems && !_droppingEditor && !IsDisposed)
            {
                Keep(oldValue);
            }
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnEditorPropertyChanged;
            MarkProblem(newValue.Scope, newValue.Definition.Id);
        }

        SaveCommand.NotifyCanExecuteChanged();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PolicyEditorViewModel.HasProblems) or nameof(PolicyEditorViewModel.Problems))
        {
            if (sender is PolicyEditorViewModel editor)
            {
                MarkProblem(editor.Scope, editor.Definition.Id);
            }

            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>A valid setting of the editor goes into the half's document.</summary>
    private void Apply(PolicyEditorViewModel editor, PolicySetting setting)
    {
        if (!ReferenceEquals(editor, Editor) || !editor.CanEdit || Half(editor.Scope) is not { Error: null } half)
        {
            return;
        }

        var wasConfigured = PolicyEngine.Read(editor.Definition.Policy, half.Document.View, _ownership.OwnedNames).State != PolicyState.NotConfigured;
        PolicyEngine.Write(editor.Definition.Policy, setting, half.Document, _ownership.OwnedNames);
        var isConfigured = PolicyEngine.Read(editor.Definition.Policy, half.Document.View, _ownership.OwnedNames).State != PolicyState.NotConfigured;
        _configured += (isConfigured ? 1 : 0) - (wasConfigured ? 1 : 0);
        UpdateLoadedText();
        _touched.Add((editor.Scope, editor.Definition.Id));
        if (_rows.TryGetValue((editor.Scope, editor.Definition.Id), out var row))
        {
            UpdateRow(row);
        }

        RebuildExtra();
        UpdateChanges();
    }

    // ---- Extra registry settings ---------------------------------------------------------------------------------------

    private List<ExtraRegistryEntryViewModel> ExtraOf(PolicyClass scope) => Half(scope) is { } half
        ? [.. half.Document.Entries.Where(e => !_ownership.IsOwned(e)).Select(e => new ExtraRegistryEntryViewModel(e, scope))]
        : [];

    private void RebuildExtra()
    {
        foreach (var root in Roots)
        {
            if (root.Children.LastOrDefault(c => c.Kind == PolicyTreeNodeKind.Extra) is { } extra)
            {
                extra.Name = Localizer.Format(L.AdministrativeTemplates.ExtraSettings, ExtraOf(root.Scope).Count);
            }
        }

        ExtraEntries = SelectedNode is { Kind: PolicyTreeNodeKind.Extra } node ? ExtraOf(node.Scope) : [];
        SelectedExtraEntry = null;
    }

    // ---- Changes -------------------------------------------------------------------------------------------------------

    private void UpdateChanges()
    {
        var count = 0;
        foreach (var (scope, id) in _touched)
        {
            if (Half(scope) is not { } half)
            {
                continue;
            }

            if (id.StartsWith("extra:", StringComparison.Ordinal))
            {
                count++;
            }
            else if (_set.Find(id) is { } definition
                && !Equals(PolicyEngine.Read(definition.Policy, half.OriginalDocument.View, _ownership.OwnedNames), PolicyEngine.Read(definition.Policy, half.Document.View, _ownership.OwnedNames)))
            {
                count++;
            }
        }

        ChangedCount = HasChanges ? Math.Max(count, 1) : 0;
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    private void ForgetTouched(PolicyClass scope) => _touched.RemoveWhere(t => t.Scope == scope);

    /// <summary>Remembers what a save asks to write into a half (a half not saved keeps what an earlier save wrote).</summary>
    private void Remember(PolicyClass scope, RegistryPolicySave? save)
    {
        if (save is not null)
        {
            _saved[scope] = save.Content;
        }
    }

    /// <summary>"Ustawienia: N, skonfigurowane: M" (after the load and after every edit).</summary>
    private void UpdateLoadedText()
    {
        if (_gpo is not null)
        {
            LoadedText = Localizer.Format(L.AdministrativeTemplates.Loaded, _set.PolicyCount, _configured);
        }
    }

    private int CountConfigured() =>
        _set.Policies.Sum(d => (d.Policy.AppliesTo(PolicyClass.Machine) && _machine is { } m && PolicyEngine.Read(d.Policy, m.Document.View, _ownership.OwnedNames).State != PolicyState.NotConfigured ? 1 : 0)
            + (d.Policy.AppliesTo(PolicyClass.User) && _user is { } u && PolicyEngine.Read(d.Policy, u.Document.View, _ownership.OwnedNames).State != PolicyState.NotConfigured ? 1 : 0));

    /// <summary>The extensions the configured policies of a half name (ADMX <c>clientExtension</c>).</summary>
    private IReadOnlyCollection<Guid> ClientExtensions(RegistryHalf half)
    {
        var result = new HashSet<Guid>();
        foreach (var definition in _set.Policies.Where(d => d.Policy.AppliesTo(half.Scope)
            && (d.Policy.ClientExtension is not null || d.Policy.Elements.Any(e => e.ClientExtension is not null))))
        {
            var setting = PolicyEngine.Read(definition.Policy, half.Document.View, _ownership.OwnedNames);
            if (setting.State == PolicyState.NotConfigured)
            {
                continue;
            }

            if (definition.Policy.ClientExtension is { } cse)
            {
                result.Add(cse);
            }

            result.UnionWith(definition.Policy.Elements.Select(e => e.ClientExtension).OfType<Guid>());
        }

        return result;
    }

    private RegistryHalf? Half(PolicyClass scope) => scope == PolicyClass.User ? _user : _machine;

    /// <summary>
    /// A problem of the templates in the UI language. The detail of an unreadable file is the parser's English message:
    /// the localized reason is shown instead (the read failed, the file is too large, or it is damaged).
    /// </summary>
    private string IssueText(PolicyDefinitionIssue issue) => Localizer.Format(issue.Kind switch
    {
        PolicyDefinitionIssueKind.FileUnreadable => L.AdministrativeTemplates.IssueFileUnreadable,
        PolicyDefinitionIssueKind.LanguageMissing => L.AdministrativeTemplates.IssueLanguageMissing,
        PolicyDefinitionIssueKind.LanguageUnreadable => L.AdministrativeTemplates.IssueLanguageUnreadable,
        PolicyDefinitionIssueKind.LanguageOutdated => L.AdministrativeTemplates.IssueLanguageOutdated,
        PolicyDefinitionIssueKind.NamespaceCollision => L.AdministrativeTemplates.IssueNamespaceCollision,
        PolicyDefinitionIssueKind.Duplicate => L.AdministrativeTemplates.IssueDuplicate,
        PolicyDefinitionIssueKind.UnknownCategory => L.AdministrativeTemplates.IssueUnknownCategory,
        PolicyDefinitionIssueKind.CategoryLoop => L.AdministrativeTemplates.IssueCategoryLoop,
        PolicyDefinitionIssueKind.CategoryTooDeep => L.AdministrativeTemplates.IssueCategoryTooDeep,
        _ => L.AdministrativeTemplates.IssueTooManyFiles,
    }, issue.FileName, issue.Kind is PolicyDefinitionIssueKind.FileUnreadable or PolicyDefinitionIssueKind.LanguageUnreadable
        ? UnreadableReason(issue.FileName)
        : issue.Detail ?? string.Empty);

    private string UnreadableReason(string path) => Localizer[_fileProblems.TryGetValue(path, out var problem)
        ? problem == PolicyFileReadProblem.TooLarge ? L.AdministrativeTemplates.FileTooLarge : L.AdministrativeTemplates.FileReadFailed
        : L.AdministrativeTemplates.FileMalformed];

    /// <summary>One Registry.pol of the GPO: as read (original) and as edited.</summary>
    private sealed record RegistryHalf(PolicyClass Scope, byte[]? Original, RegistryPolicyDocument OriginalDocument, RegistryPolicyDocument Document, string? Error)
    {
        /// <summary>The file changed on SYSVOL under the edits.</summary>
        public bool Conflict { get; init; }

        public bool IsChanged => Error is null && !SameContent(Original, Document.ToBytes());

        public static RegistryHalf Of(PolicyClass scope, byte[]? content)
        {
            var original = RegistryPolicyDocument.Parse(content);
            return new RegistryHalf(scope, content, original, original.Clone(), null);
        }

        public static RegistryHalf Unreadable(PolicyClass scope, string error) =>
            new(scope, null, new RegistryPolicyDocument(), new RegistryPolicyDocument(), error);

        /// <summary>Back to the original (the edits are dropped).</summary>
        public RegistryHalf Discarded() => this with { Document = OriginalDocument.Clone(), Conflict = false };

        /// <summary>The same edits on top of <paramref name="content"/>, now the file on SYSVOL (what this tab saved).</summary>
        public RegistryHalf Rebased(byte[]? content) =>
            this with { Original = content, OriginalDocument = RegistryPolicyDocument.Parse(content), Conflict = false };

        /// <summary>Same content; a missing file, an empty one and one without entries are the same.</summary>
        public static bool SameContent(byte[]? a, byte[]? b)
        {
            var left = a is { Length: > 8 } ? a : null;
            var right = b is { Length: > 8 } ? b : null;
            return left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
        }
    }
}
