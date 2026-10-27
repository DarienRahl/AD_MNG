using System.Collections;
using System.Globalization;
using System.Text.Json;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Filters;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.AccountReview;

/// <summary>The scope of a run as the account review resolved it (base, scope, server filter, client checks).</summary>
/// <param name="BaseDn">Search base.</param>
/// <param name="Scope">Search scope.</param>
/// <param name="Filter">Server filter of the scope (saved view, group members, saved filters), null for none.</param>
/// <param name="ClientPlan">Saved filters the client checks (constructed attributes), null for none.</param>
public sealed record ReviewScope(string BaseDn, SearchScope Scope, string? Filter, SavedFilterPlan? ClientPlan);

/// <summary>
/// The "Uprawnienia" tab of the account review (README U4, design docs/dev/u4-privileged-access.md): every account of the
/// window's scope that has any right beyond a plain domain user, with a level per finding and the path that explains it.
/// Runs with the window's scope and "Sprawdź"; streamed and cancellable, results only in memory, CSV only on demand, every
/// action through the change plan. The settings of the tab (levels, marks, rules, peers) are saved as they change; they
/// are shared by every connection window, so a change saved by another window's account review is shown here at once.
/// </summary>
public sealed partial class PrivilegedAccessViewModel : ObservableObject, IDisposable
{
    private const int FirstBatchSize = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>The sources U4a evaluates; grant sources add theirs (U4b, U4c).</summary>
    private static readonly PrivilegeSource[] BuiltInSources =
        [PrivilegeSource.Groups, PrivilegeSource.OperatorRules, PrivilegeSource.AccountSettings, PrivilegeSource.Peers];

    private readonly DialogServices _services;
    private readonly Func<IDirectorySession?> _session;
    private readonly Func<bool> _isSessionEnded;
    private readonly FilterLibrary _filters;
    private readonly ICsvCodec _csv;
    private readonly IUserMessageLauncher? _messages;
    private readonly IReadOnlyList<IPrivilegeGrantSource> _sources;
    private readonly Dictionary<string, AccountPrivileges> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PrivilegedAccountRowViewModel> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingRefresh = new(StringComparer.Ordinal);
    private readonly Debouncer _refreshDebouncer;
    private RunContext? _run;
    private Task _refreshing = Task.CompletedTask;
    private int _generation;
    private int _readCount;
    private IList? _selectedItems;
    private string _sortColumn = PrivilegedAccessColumns.Level;
    private bool _sortDescending = true;
    private bool _loadingSettings;
    private bool _savingSettings;
    private string _settingsSnapshot = string.Empty;
    private bool _disposed;

    internal PrivilegedAccessViewModel(
        DialogServices services,
        Func<IDirectorySession?> session,
        Func<bool> isSessionEnded,
        FilterLibrary filters,
        ICsvCodec csv,
        IUserMessageLauncher? messages,
        IEnumerable<IPrivilegeGrantSource>? sources = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _isSessionEnded = isSessionEnded ?? throw new ArgumentNullException(nameof(isSessionEnded));
        _filters = filters ?? throw new ArgumentNullException(nameof(filters));
        _csv = csv ?? throw new ArgumentNullException(nameof(csv));
        _messages = messages;
        _sources = [.. sources ?? []];
        _refreshDebouncer = new Debouncer(services.Time, TimeSpan.FromMilliseconds(300));
        LevelTiles = [.. PrivilegeLevels.Descending.Select(l => new PrivilegeLevelTileViewModel(l))];
        foreach (var tile in LevelTiles)
        {
            tile.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PrivilegeLevelTileViewModel.IsActive))
                {
                    RebuildItems();
                }
            };
        }

        SourceOptions = [.. BuiltInSources.Concat(_sources.Select(s => s.Source)).Distinct().Order().Select(s => new PrivilegeSourceOptionViewModel(s))];
        foreach (var option in SourceOptions)
        {
            option.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PrivilegeSourceOptionViewModel.IsActive))
                {
                    RebuildItems();
                }
            };
        }

        LoadSettings();
        RefreshTexts();
        _settingsSnapshot = SettingsSnapshot();
        _services.Changes.Changed += OnDirectoryChanged;
        _filters.Changed += OnFiltersChanged;
        _services.Settings.Changed += OnSettingsChanged;
    }

    private ILocalizer Localizer => _services.Localizer;

    // ---- Settings of the tab -------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IncludeComputers { get; set; }

    [ObservableProperty]
    public partial bool IncludeServiceAccounts { get; set; }

    /// <summary>U4b: "Głębokie skanowanie" - the ACL delegations also read every user, group and computer (slower).</summary>
    [ObservableProperty]
    public partial bool DeepAclScan { get; set; }

    /// <summary>The ACL delegations source (U4b) is registered: its settings are shown.</summary>
    public bool HasAclSource => _sources.Any(s => s.Source == PrivilegeSource.DirectoryAcl);

    [ObservableProperty]
    public partial bool PeersByOu { get; set; }

    [ObservableProperty]
    public partial bool PeersByDepartment { get; set; }

    [ObservableProperty]
    public partial bool PeersByTitle { get; set; }

    /// <summary>"Co najmniej N kont do porównania".</summary>
    [ObservableProperty]
    public partial int PeersMinimum { get; set; }

    /// <summary>"Nietypowa grupa: mniej niż N % kont".</summary>
    [ObservableProperty]
    public partial int PeersShare { get; set; }

    /// <summary>The level options, highest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Option<PrivilegeLevel>> LevelOptions { get; private set; } = [];

    /// <summary>The catalogue groups with their levels (the operator may change them).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PrivilegedGroupLevelViewModel> GroupLevels { get; private set; } = [];

    /// <summary>Groups and accounts marked as privileged.</summary>
    public BulkObservableCollection<PrivilegedMarkViewModel> Marks { get; } = [];

    /// <summary>Saved filters used as "mark as privileged".</summary>
    public BulkObservableCollection<PrivilegedRuleViewModel> Rules { get; } = [];

    /// <summary>The saved filters (for the rules).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    public partial IReadOnlyList<Option<string>> FilterOptions { get; private set; } = [];

    public bool HasFilters => FilterOptions.Count > 0;

    // ---- Results -------------------------------------------------------------------------------------------------

    /// <summary>Tier 0 / wysoki / podwyższony / drobny with the number of accounts; toggles filter the list.</summary>
    public IReadOnlyList<PrivilegeLevelTileViewModel> LevelTiles { get; }

    /// <summary>The sources with the number of accounts; toggles filter the list.</summary>
    public IReadOnlyList<PrivilegeSourceOptionViewModel> SourceOptions { get; }

    /// <summary>Only accounts unusual among their peers.</summary>
    [ObservableProperty]
    public partial bool OnlyPeerOutliers { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasRun { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>
    /// What the grant sources of the last run could not read or evaluate (U4c: SYSVOL unavailable - e.g. the session's
    /// credentials cannot open the share -, unreadable GPO files, unknown names), one line each; empty when all was read.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotices))]
    public partial string NoticeText { get; private set; } = string.Empty;

    public bool HasNotices => NoticeText.Length > 0;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? AsOf { get; private set; }

    /// <summary>Number of accounts with at least one finding (the link on the U2 tile).</summary>
    [ObservableProperty]
    public partial int AccountCount { get; private set; }

    /// <summary>The accounts that pass the level and source filters, sorted.</summary>
    public BulkObservableCollection<PrivilegedAccountRowViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial PrivilegedAccountRowViewModel? SelectedItem { get; set; }

    /// <summary>The list's multi-selection (set by the view).</summary>
    public IList? SelectedItems
    {
        get => _selectedItems;
        set
        {
            _selectedItems = value;
            NotifySelectionChanged();
        }
    }

    /// <summary>The selected rows (empty when nothing is selected).</summary>
    public IReadOnlyList<PrivilegedAccountRowViewModel> Selection =>
        _selectedItems is { Count: > 0 } items ? [.. items.OfType<PrivilegedAccountRowViewModel>()]
        : SelectedItem is { } row ? [row]
        : [];

    [ObservableProperty]
    public partial string SelectionText { get; private set; } = string.Empty;

    /// <summary>The findings of <see cref="SelectedItem"/> (details pane).</summary>
    public BulkObservableCollection<PrivilegeFindingViewModel> Findings { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromGroupCommand))]
    [NotifyPropertyChangedFor(nameof(RemoveFromGroupHint))]
    public partial PrivilegeFindingViewModel? SelectedFinding { get; set; }

    /// <summary>"Uprawnienia: Henryk Żuławski" or the hint to select an account.</summary>
    [ObservableProperty]
    public partial string DetailsTitle { get; private set; } = string.Empty;

    /// <summary>Why "Usuń z grupy…" is not possible for the selected finding (primary group), else empty.</summary>
    public string RemoveFromGroupHint => SelectedFinding is { Finding.IsThroughPrimaryGroup: true }
        ? Localizer[L.PrivilegedAccess.PrimaryGroupNotRemovable]
        : string.Empty;

    public bool IsEmpty => HasRun && !IsRunning && Items.Count == 0;

    /// <summary>The columns of the list (headers in the UI language).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AccountReviewColumnViewModel> Columns { get; private set; } = [];

    /// <summary>"Wyślij wiadomość…" (U1) only with a registered launcher.</summary>
    public bool CanSendMessages => _messages is not null;

    /// <summary>The pending refresh of rows changed through the pipeline.</summary>
    public Task Refreshing => _refreshing;

    // ---- Commands ------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void Sort(string? column)
    {
        if (string.IsNullOrEmpty(column))
        {
            return;
        }

        _sortDescending = column == _sortColumn ? !_sortDescending : column is PrivilegedAccessColumns.Level or PrivilegedAccessColumns.Findings;
        _sortColumn = column;
        RebuildItems();
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync(PrivilegedAccountRowViewModel? row)
    {
        row ??= SelectedItem;
        return row is null || _isSessionEnded() ? Task.CompletedTask : _services.Dialogs.ShowPropertiesAsync(row.Dn);
    }

    private bool CanOpen(PrivilegedAccountRowViewModel? row) => !_isSessionEnded();

    [RelayCommand(CanExecute = nameof(HasRows))]
    private Task BatchEditAsync()
    {
        var rows = Targets();
        return rows.Count == 0 || _session() is null
            ? Task.CompletedTask
            : _services.Dialogs.ShowBatchEditorAsync(new BatchEditorRequest([.. rows.Select(r => r.Dn)]) { Title = Localizer[L.Commands.BatchEdit] });
    }

    /// <summary>The shown list (all rows, in the shown order) with the findings, to a CSV file the operator chooses.</summary>
    [RelayCommand(CanExecute = nameof(HasItems))]
    private async Task ExportCsvAsync()
    {
        if (Items.Count == 0)
        {
            return;
        }

        var title = Localizer[L.AccountReview.ExportCsv];
        var path = await _services.Files.SaveFileAsync(
            title,
            ListExportSnapshot.SuggestFileName(Localizer[L.PrivilegedAccess.ExportTitle], _services.Time.GetLocalNow()),
            [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var columns = PrivilegedAccessColumns.All;
        var headers = columns.Select(c => Localizer[PrivilegedAccessText.ColumnKey(c)]).ToList();
        var rows = Items.Select(r => (IReadOnlyList<string>)[.. columns.Select(c => r[c])]).ToList();
        var delimiter = CsvFiles.ListSeparator(_services.Regional.Culture);
        try
        {
            await CsvFiles.WriteAsync(path, writer => _csv.Write(writer, headers, rows, delimiter, protectFormulas: true)).ConfigureAwait(true);
            _services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Files.Exported, rows.Count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _services.Dialogs.ShowErrorAsync(title, _services.Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// "Usuń z grupy…": removes the selected accounts that have this direct group on a path from it (batch preview). The
    /// finding may stay through another path; the row is re-read afterwards.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveFromGroup))]
    private Task RemoveFromGroupAsync()
    {
        if (SelectedFinding?.Finding.DirectGroupDn is not { } group || SelectedItem is not { } current)
        {
            return Task.CompletedTask;
        }

        var key = DnKey.For(group);
        var rows = Selection.Where(r => r.Facts.Findings.Any(f => f.DirectGroupDn is { } dn && DnKey.For(dn) == key)).ToList();
        if (!rows.Contains(current))
        {
            rows.Insert(0, current);
        }

        var name = DistinguishedName.TryParse(group, out var parsed) && !parsed.IsRoot ? parsed.Name : group;
        return ApplyAsync(rows, new RemoveFromGroupsOperation([group]), Localizer.Format(L.PrivilegedAccess.RemoveFromGroupTitle, name), preview: true);
    }

    private bool CanRemoveFromGroup() => SelectedFinding is { CanRemoveFromGroup: true } && CanChange();

    /// <summary>"Wyczyść adminCount…": accounts with an orphaned adminCount (SDProp sets it again on protected members).</summary>
    [RelayCommand(CanExecute = nameof(CanClearAdminCount))]
    private Task ClearAdminCountAsync() =>
        ApplyAsync(OrphanedTargets(), new ClearAttributeOperation(Attr.AdminCount), Localizer[L.PrivilegedAccess.ClearAdminCount], preview: true);

    private bool CanClearAdminCount() => CanChange() && OrphanedTargets().Count > 0;

    [RelayCommand(CanExecute = nameof(HasSelectionForChange))]
    private Task DisableAsync() => ApplyAsync(Selection, new SetAccountEnabledOperation(false), Localizer[L.Commands.Disable]);

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private Task SendMessageAsync()
    {
        var rows = Targets().Where(r => r.Facts.Type == PrivilegedAccountType.User).ToList();
        return _messages is null || rows.Count == 0 ? Task.CompletedTask : _messages.ShowComposerAsync([.. rows.Select(r => r.Dn)], null);
    }

    private bool CanSendMessage() => CanSendMessages && HasRows();

    /// <summary>"Oznacz grupę…": groups picked in the session are marked as privileged (level High, editable).</summary>
    [RelayCommand(CanExecute = nameof(CanPick))]
    private Task MarkGroupAsync() => MarkAsync([ObjectKind.Group]);

    /// <summary>"Oznacz konto…": accounts picked in the session are marked as privileged.</summary>
    [RelayCommand(CanExecute = nameof(CanPick))]
    private Task MarkAccountAsync() => MarkAsync([ObjectKind.User, ObjectKind.Computer, ObjectKind.GroupManagedServiceAccount]);

    private bool CanPick() => _session() is not null && !_isSessionEnded();

    [RelayCommand]
    private Task RemoveMarkAsync(PrivilegedMarkViewModel? mark)
    {
        if (mark is null || !Marks.Remove(mark))
        {
            return Task.CompletedTask;
        }

        mark.Changed -= OnSettingChanged;
        return SaveSettingsAsync();
    }

    /// <summary>"Dodaj regułę": a rule with the first saved filter; without filters the builder opens first.</summary>
    [RelayCommand]
    private async Task AddRuleAsync()
    {
        var filterId = FilterOptions.FirstOrDefault()?.Value;
        if (filterId is null)
        {
            if (await SavedFiltersViewModel.CreateOrEditAsync(_services, _filters, null).ConfigureAwait(true) is not { } created)
            {
                return;
            }

            RefreshFilterOptions();
            filterId = created.Id;
        }

        var rule = CreateRule(new PrivilegedRuleSetting { FilterId = filterId });
        Rules.Add(rule);
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private Task RemoveRuleAsync(PrivilegedRuleViewModel? rule)
    {
        if (rule is null || !Rules.Remove(rule))
        {
            return Task.CompletedTask;
        }

        rule.Changed -= OnSettingChanged;
        return SaveSettingsAsync();
    }

    [RelayCommand]
    private async Task NewFilterAsync()
    {
        if (await SavedFiltersViewModel.CreateOrEditAsync(_services, _filters, null).ConfigureAwait(true) is not null)
        {
            RefreshFilterOptions();
        }
    }

    [RelayCommand]
    private Task ManageFiltersAsync() => _services.Dialogs.ShowSavedFiltersAsync();

    /// <summary>Called by the view when the list's selection changed.</summary>
    public void NotifySelectionChanged()
    {
        var count = Selection.Count;
        SelectionText = count > 1 ? Localizer.Format(L.AccountReview.Selected, count) : string.Empty;
        RefreshCommandStates();
    }

    // ---- Run -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A run over the scope: the queries of <see cref="PrivilegedAccessScanner"/> off the UI thread, the accounts streamed
    /// in batches, then the peer comparison. A newer run or <paramref name="token"/> stops it (what was read stays).
    /// </summary>
    internal async Task RunAsync(IDirectorySession session, ReviewScope scope, DomainPolicy policy, DateTimeOffset now, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(scope);
        var generation = ++_generation;
        _accounts.Clear();
        _rows.Clear();
        _pendingRefresh.Clear();
        _readCount = 0;
        _run = null;
        Items.ReplaceAll([]);
        Error = null;
        NoticeText = string.Empty;
        HasRun = true;
        AsOf = now;
        IsRunning = true;
        UpdateCounts();
        UpdateStatus();
        var stopped = false;
        try
        {
            await SaveSettingsAsync().ConfigureAwait(true);
            var options = BuildOptions(session, policy);
            var scanner = new PrivilegedAccessScanner(session, options, _sources);
            var evaluator = await Task.Run(() => scanner.PrepareAsync(now, token), token).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            var run = new RunContext(session, scanner, evaluator, scope, new PrivilegedAccessFormatter(Localizer, session.Info.DomainDnsName));
            _run = run;
            RefreshNotices(run);
            if (scanner.SkippedRules.Count > 0)
            {
                _services.Notifications.Show(NotificationSeverity.Warning, Localizer.Format(L.PrivilegedAccess.SkippedRules, string.Join(", ", scanner.SkippedRules)));
            }

            await StreamAsync(run, generation, token).ConfigureAwait(true);
            if (generation == _generation)
            {
                StatusText = Localizer[L.PrivilegedAccess.StatusPeers];
                List<AccountPrivileges> snapshot = [.. _accounts.Values];
                var peers = await Task.Run(() => PrivilegedAccessScanner.ComparePeers(snapshot, evaluator), token).ConfigureAwait(true);
                if (generation == _generation)
                {
                    ApplyPeers(peers);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            stopped = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation == _generation)
            {
                Error = _services.Errors.Translate(ex);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsRunning = false;
                UpdateCounts();
                RebuildItems();
                UpdateStatus(stopped);
            }
        }
    }

    /// <summary>The session ended: the results stay (read-only), actions are refused.</summary>
    internal void OnSessionEnded()
    {
        _refreshDebouncer.Cancel();
        RefreshCommandStates();
    }

    internal void OnCultureChanged()
    {
        RefreshTexts();
        if (_run is { } run)
        {
            var formatter = new PrivilegedAccessFormatter(Localizer, run.Session.Info.DomainDnsName);
            run.Formatter = formatter;
            foreach (var row in _rows.Values)
            {
                row.UseFormatter(formatter);
            }

            ShowDetails(SelectedItem);
            RefreshNotices(run);
        }

        UpdateStatus();
        NotifySelectionChanged();
    }

    private void RefreshNotices(RunContext run) =>
        NoticeText = string.Join(Environment.NewLine, run.Scanner.Notices.Select(run.Formatter.Notice));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshDebouncer.Cancel();
        _services.Changes.Changed -= OnDirectoryChanged;
        _filters.Changed -= OnFiltersChanged;
        _services.Settings.Changed -= OnSettingsChanged;
    }

    partial void OnSelectedItemChanged(PrivilegedAccountRowViewModel? value)
    {
        ShowDetails(value);
        NotifySelectionChanged();
    }

    partial void OnOnlyPeerOutliersChanged(bool value) => RebuildItems();

    partial void OnIncludeComputersChanged(bool value) => SaveFromUi();

    partial void OnIncludeServiceAccountsChanged(bool value) => SaveFromUi();

    partial void OnDeepAclScanChanged(bool value) => SaveFromUi();

    partial void OnPeersByOuChanged(bool value) => SaveFromUi();

    partial void OnPeersByDepartmentChanged(bool value) => SaveFromUi();

    partial void OnPeersByTitleChanged(bool value) => SaveFromUi();

    partial void OnPeersMinimumChanged(int value) => SaveFromUi();

    partial void OnPeersShareChanged(int value) => SaveFromUi();

    private Task StreamAsync(RunContext run, int generation, CancellationToken token)
    {
        var time = _services.Time;
        var plan = run.Scope.ClientPlan;
        return Task.Run(
            async () =>
            {
                var buffer = new List<AccountPrivileges>(FirstBatchSize);
                var read = 0;
                var lastFlush = time.GetTimestamp();
                try
                {
                    await foreach (var account in run.Scanner.ScanAsync(run.Evaluator, run.Scope.BaseDn, run.Scope.Scope, run.Scope.Filter, plan?.Attributes, token)
                        .WithCancellation(token).ConfigureAwait(false))
                    {
                        read++;
                        if (plan is { NeedsClientCheck: true } && !plan.Matches(account.Account, dn => run.Evaluator.IsMemberOf(account.Account, dn)))
                        {
                            continue;
                        }

                        buffer.Add(account);
                        if (buffer.Count >= FirstBatchSize || time.GetElapsedTime(lastFlush) >= FlushInterval)
                        {
                            await FlushAsync(buffer, read, generation).ConfigureAwait(false);
                            buffer = new List<AccountPrivileges>(FirstBatchSize);
                            lastFlush = time.GetTimestamp();
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    await FlushAsync(buffer, read, generation).ConfigureAwait(false);
                    throw;
                }

                await FlushAsync(buffer, read, generation).ConfigureAwait(false);
            },
            token);
    }

    private Task FlushAsync(List<AccountPrivileges> batch, int read, int generation) => _services.Dispatcher.InvokeAsync(() =>
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        _readCount = read;
        var added = new List<PrivilegedAccountRowViewModel>();
        foreach (var account in batch)
        {
            _accounts[DnKey.For(account.DistinguishedName)] = account;
            if (Place(account) is { } row && PassesFilters(row))
            {
                added.Add(row);
            }
        }

        Items.AddRange(added);
        UpdateCounts();
        UpdateStatus();
    });

    private void ApplyPeers(IReadOnlyDictionary<string, IReadOnlyList<PrivilegeFinding>> peers)
    {
        foreach (var (key, account) in _accounts.ToList())
        {
            var found = peers.TryGetValue(PrivilegedAccessScanner.KeyOf(account.DistinguishedName), out var list) ? list : [];
            if (found.Count == 0 && !account.IsPeerOutlier)
            {
                continue;
            }

            var updated = account.WithPeerFindings(found);
            _accounts[key] = updated;
            Place(updated);
        }
    }

    /// <summary>Adds, updates or removes the row of an account; returns a new row (the caller adds it to the list).</summary>
    private PrivilegedAccountRowViewModel? Place(AccountPrivileges account)
    {
        var key = DnKey.For(account.DistinguishedName);
        _rows.TryGetValue(key, out var existing);
        if (!account.HasFindings)
        {
            if (existing is not null)
            {
                _rows.Remove(key);
                Items.Remove(existing);
            }

            return null;
        }

        if (existing is not null)
        {
            existing.Update(account);
            if (ReferenceEquals(existing, SelectedItem))
            {
                ShowDetails(existing);
            }

            return null;
        }

        var row = new PrivilegedAccountRowViewModel(account, _run?.Formatter ?? new PrivilegedAccessFormatter(Localizer, string.Empty));
        _rows[key] = row;
        return row;
    }

    private PrivilegedAccessOptions BuildOptions(IDirectorySession session, DomainPolicy policy)
    {
        var settings = _services.Settings.Current.AccountReview.Privileges;
        var nc = session.Info.DefaultNamingContext;
        var singleDomainForest = session.Info.RootDomainNamingContext is null
            || DistinguishedName.AreEqual(session.Info.RootDomainNamingContext, nc);
        var rules = new List<PrivilegeConditionRule>();
        foreach (var rule in settings.Rules)
        {
            if (_filters.Find(rule.FilterId) is { } filter)
            {
                rules.Add(new PrivilegeConditionRule(filter.Name, filter.Definition, rule.Level, rule.Target, rule.Note));
            }
        }

        return new PrivilegedAccessOptions
        {
            DomainSid = policy.DomainSid,
            RootDomainSid = singleDomainForest ? policy.DomainSid : null,
            LevelOverrides = settings.OverrideMap(),
            Marks = [.. settings.Marks.Select(m => new PrivilegeMark(
                m.Sid, m.Dn?.Replace(Views.SavedView.DomainPlaceholder, nc, StringComparison.OrdinalIgnoreCase), m.Level, m.Note))],
            Rules = rules,
            IncludeComputers = settings.IncludeComputers,
            IncludeServiceAccounts = settings.IncludeServiceAccounts,
            DeepAclScan = settings.DeepAclScan,
            Peers = settings.Peers,
            PageSize = _services.Settings.Current.PageSize,
        };
    }

    // ---- Refresh after changes ---------------------------------------------------------------------------------

    private void OnDirectoryChanged(object? sender, DirectoryChangesEventArgs e) => _ = _services.Dispatcher.RunAsync(() =>
    {
        if (_disposed || _run is null)
        {
            return;
        }

        var changed = false;
        foreach (var change in e.Changes)
        {
            var key = DnKey.For(change.DistinguishedName);
            if (!_accounts.ContainsKey(key))
            {
                continue;
            }

            if (change.Kind == DirectoryChangeKind.Deleted)
            {
                _accounts.Remove(key);
                Place(EmptyOf(change.DistinguishedName));
                changed = true;
            }
            else if (change.Kind == DirectoryChangeKind.Moved && change.NewDistinguishedName is { } moved)
            {
                _accounts.Remove(key);
                Place(EmptyOf(change.DistinguishedName));
                _pendingRefresh.Add(moved);
                changed = true;
            }
            else
            {
                _pendingRefresh.Add(change.DistinguishedName);
            }
        }

        if (changed)
        {
            UpdateCounts();
        }

        if (_pendingRefresh.Count > 0)
        {
            _refreshing = _refreshDebouncer.RunAsync(RefreshPendingAsync);
        }
    });

    private async Task RefreshPendingAsync(CancellationToken token)
    {
        if (_run is not { } run || _session() is not { } session || !ReferenceEquals(session, run.Session) || IsRunning)
        {
            _pendingRefresh.Clear();
            return;
        }

        var dns = _pendingRefresh.ToList();
        _pendingRefresh.Clear();
        try
        {
            var attributes = run.Evaluator.AccountAttributes.Concat(run.Scope.ClientPlan?.Attributes ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var found = await Task.Run(() => DirectoryLookup.ReadManyAsync(session, dns, attributes, token), token).ConfigureAwait(true);
            var evaluated = found.Values
                .Select(run.Evaluator.Evaluate)
                .OfType<AccountPrivileges>()
                .Where(a => run.Scope.ClientPlan is not { NeedsClientCheck: true } plan || plan.Matches(a.Account, dn => run.Evaluator.IsMemberOf(a.Account, dn)))
                .ToList();
            if (!ReferenceEquals(run, _run))
            {
                return;
            }

            foreach (var dn in dns)
            {
                _accounts.Remove(DnKey.For(dn));
            }

            foreach (var account in evaluated)
            {
                _accounts[DnKey.For(account.DistinguishedName)] = account;
            }

            foreach (var dn in dns.Where(d => !found.ContainsKey(DnKey.For(d))))
            {
                Place(EmptyOf(dn));
            }

            List<AccountPrivileges> snapshot = [.. _accounts.Values];
            var peers = await Task.Run(() => PrivilegedAccessScanner.ComparePeers(snapshot, run.Evaluator), token).ConfigureAwait(true);
            if (!ReferenceEquals(run, _run))
            {
                return;
            }

            foreach (var account in evaluated)
            {
                Place(account);
            }

            ApplyPeers(peers);
            UpdateCounts();
            RebuildItems();
            UpdateStatus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded or closed.
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            _services.Notifications.Show(NotificationSeverity.Warning, _services.Errors.Translate(ex));
        }
    }

    /// <summary>A stand-in without findings: <see cref="Place"/> removes the row of the DN.</summary>
    private static AccountPrivileges EmptyOf(string dn) => new()
    {
        Account = new DirectoryObject(dn, []),
        Type = PrivilegedAccountType.User,
        Findings = [],
    };

    // ---- Settings ------------------------------------------------------------------------------------------------

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            var settings = _services.Settings.Current.AccountReview.Privileges;
            IncludeComputers = settings.IncludeComputers;
            IncludeServiceAccounts = settings.IncludeServiceAccounts;
            DeepAclScan = settings.DeepAclScan;
            var peers = settings.Peers.Normalized();
            PeersByOu = peers.ByOrganizationalUnit;
            PeersByDepartment = peers.ByDepartment;
            PeersByTitle = peers.ByTitle;
            PeersMinimum = peers.MinPeers;
            PeersShare = peers.MaxSharePercent;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    /// <summary>Option lists and the settings collections in the UI language (rebuilt from the settings).</summary>
    private void RefreshTexts()
    {
        _loadingSettings = true;
        try
        {
            LevelOptions = [.. PrivilegeLevels.Descending.Select(l => new Option<PrivilegeLevel>(l, Localizer[PrivilegedAccessText.LevelKey(l)]))];
            var settings = _services.Settings.Current.AccountReview.Privileges;
            var overrides = settings.OverrideMap();
            foreach (var group in GroupLevels)
            {
                group.Changed -= OnSettingChanged;
            }

            GroupLevels = [.. PrivilegedGroupCatalog.All.Select(g => new PrivilegedGroupLevelViewModel(
                g, LevelOptions, overrides.TryGetValue(g.Id, out var level) ? PrivilegeLevels.Normalize(level) : g.DefaultLevel, Localizer))];
            foreach (var group in GroupLevels)
            {
                group.Changed += OnSettingChanged;
            }

            foreach (var mark in Marks)
            {
                mark.Changed -= OnSettingChanged;
            }

            Marks.ReplaceAll(settings.Marks.Select(CreateMark));
            RefreshFilterOptions();
            foreach (var tile in LevelTiles)
            {
                tile.Title = Localizer[PrivilegedAccessText.LevelKey(tile.Level)];
                tile.Hint = Localizer[PrivilegedAccessText.LevelHintKey(tile.Level)];
            }

            foreach (var option in SourceOptions)
            {
                option.Title = Localizer[PrivilegedAccessText.SourceKey(option.Source)];
            }

            Columns = [.. PrivilegedAccessColumns.Shown.Select(c => new AccountReviewColumnViewModel(c, Localizer[PrivilegedAccessText.ColumnKey(c)]))];
            UpdateCounts();
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void RefreshFilterOptions()
    {
        var wasLoading = _loadingSettings;
        _loadingSettings = true;
        try
        {
            FilterOptions = [.. _filters.Filters
                .OrderBy(f => f.Name, StringComparer.Create(Localizer.Culture, ignoreCase: true))
                .Select(f => new Option<string>(f.Id, f.Name))];
            foreach (var rule in Rules)
            {
                rule.Changed -= OnSettingChanged;
            }

            Rules.ReplaceAll(_services.Settings.Current.AccountReview.Privileges.Rules.Select(CreateRule));
        }
        finally
        {
            _loadingSettings = wasLoading;
        }
    }

    private PrivilegedMarkViewModel CreateMark(PrivilegedMarkSetting setting)
    {
        var mark = new PrivilegedMarkViewModel(setting, LevelOptions);
        mark.Changed += OnSettingChanged;
        return mark;
    }

    private PrivilegedRuleViewModel CreateRule(PrivilegedRuleSetting setting)
    {
        var targets = Enum.GetValues<PrivilegeRuleTarget>().Select(t => new Option<PrivilegeRuleTarget>(t, Localizer[PrivilegedAccessText.TargetKey(t)])).ToList();
        var rule = new PrivilegedRuleViewModel(setting, FilterOptions, targets, LevelOptions);
        rule.Changed += OnSettingChanged;
        return rule;
    }

    private async Task MarkAsync(IReadOnlyList<ObjectKind> kinds)
    {
        if (_session() is not { } session)
        {
            return;
        }

        var picked = await _services.Dialogs.PickObjectsAsync(session, kinds, multiSelect: true).ConfigureAwait(true);
        if (picked.Count == 0)
        {
            return;
        }

        var nc = session.Info.DefaultNamingContext;
        var objects = await Task.Run(() => DirectoryLookup.ReadManyAsync(session, picked, [Attr.ObjectSid], CancellationToken.None)).ConfigureAwait(true);
        foreach (var dn in picked)
        {
            var sid = objects.TryGetValue(DnKey.For(dn), out var obj) ? obj.ObjectSid : null;
            var portable = Views.SavedView.ToPortableDn(dn, nc);
            if (Marks.Any(m => (sid is not null && string.Equals(m.Sid, sid, StringComparison.OrdinalIgnoreCase)) || DistinguishedName.AreEqual(m.Dn, portable)))
            {
                continue;
            }

            Marks.Add(CreateMark(new PrivilegedMarkSetting { Sid = sid, Dn = portable, Level = PrivilegeLevel.High }));
        }

        await SaveSettingsAsync().ConfigureAwait(true);
        _services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.PrivilegedAccess.MarkAdded, picked.Count));
    }

    private void OnSettingChanged(object? sender, EventArgs e) => SaveFromUi();

    private void SaveFromUi()
    {
        if (!_loadingSettings && !_disposed)
        {
            SaveSettingsAsync().Forget();
        }
    }

    private Task SaveSettingsAsync()
    {
        var overrides = GroupLevels.Where(g => g.IsChanged).Select(g => new PrivilegedGroupLevelSetting(g.Group.Id, g.Level.Value)).ToList();
        var marks = Marks.Select(m => m.ToSetting()).ToList();
        var rules = Rules.Select(r => r.ToSetting()).OfType<PrivilegedRuleSetting>().ToList();
        var peers = new PeerOptions
        {
            ByOrganizationalUnit = PeersByOu,
            ByDepartment = PeersByDepartment,
            ByTitle = PeersByTitle,
            MinPeers = PeersMinimum,
            MaxSharePercent = PeersShare,
        }.Normalized();
        var computers = IncludeComputers;
        var serviceAccounts = IncludeServiceAccounts;
        var deepAclScan = DeepAclScan;

        // The settings store raises Changed synchronously inside UpdateAsync: this tab's own write is not reloaded.
        _savingSettings = true;
        try
        {
            var saving = _services.Settings.UpdateAsync(s =>
            {
                var p = s.AccountReview.Privileges;
                p.LevelOverrides = overrides;
                p.Marks = marks;
                p.Rules = rules;
                p.Peers = peers;
                p.IncludeComputers = computers;
                p.IncludeServiceAccounts = serviceAccounts;
                p.DeepAclScan = deepAclScan;
                p.Normalize();
            });
            _settingsSnapshot = SettingsSnapshot();
            return saving;
        }
        finally
        {
            _savingSettings = false;
        }
    }

    /// <summary>
    /// The settings of the tab are shared by every connection window (copy-on-write settings): when another window's account
    /// review saved levels, marks, rules or peers, this tab shows the stored settings, so that its own next save never undoes
    /// that change. Its own saves and writes of other settings (column widths, recent containers) change nothing here.
    /// </summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed || _savingSettings)
        {
            return;
        }

        _ = _services.Dispatcher.RunAsync(() =>
        {
            var snapshot = SettingsSnapshot();
            if (_disposed || string.Equals(snapshot, _settingsSnapshot, StringComparison.Ordinal))
            {
                return;
            }

            _settingsSnapshot = snapshot;
            LoadSettings();
            RefreshTexts();
        });
    }

    /// <summary>The stored settings of the tab (their serialized form: the copy-on-write store gives new lists on every update).</summary>
    private string SettingsSnapshot() => JsonSerializer.Serialize(_services.Settings.Current.AccountReview.Privileges, JsonSettingsStore.SerializerOptions);

    private void OnFiltersChanged(object? sender, EventArgs e) => _ = _services.Dispatcher.RunAsync(() =>
    {
        if (!_disposed)
        {
            RefreshFilterOptions();
        }
    });

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private Task ApplyAsync(IReadOnlyList<PrivilegedAccountRowViewModel> rows, BatchOperation operation, string title, bool preview = false)
    {
        if (rows.Count == 0 || _session() is null || _isSessionEnded())
        {
            return Task.CompletedTask;
        }

        return _services.Coordinator.ApplyAsync(new ChangeRequest([.. rows.Select(r => r.Dn)], [operation], title) { ForcePreview = preview });
    }

    /// <summary>The selection, or the whole list when nothing is selected.</summary>
    private IReadOnlyList<PrivilegedAccountRowViewModel> Targets() => Selection is { Count: > 0 } selection ? selection : [.. Items];

    private List<PrivilegedAccountRowViewModel> OrphanedTargets() =>
        [.. Targets().Where(r => r.Facts.Findings.Any(f => f.Kind == PrivilegeFindingKind.OrphanedAdminCount))];

    private bool HasRows() => Items.Count > 0 && !_isSessionEnded();

    private bool HasItems() => Items.Count > 0;

    private bool CanChange() => !_isSessionEnded() && !IsRunning && _session() is not null;

    private bool HasSelectionForChange() => Selection.Count > 0 && CanChange();

    private bool PassesFilters(PrivilegedAccountRowViewModel row)
    {
        if (OnlyPeerOutliers && !row.IsPeerOutlier)
        {
            return false;
        }

        var levels = LevelTiles.Where(t => t.IsActive).Select(t => t.Level).ToHashSet();
        var sources = SourceOptions.Where(o => o.IsActive).Select(o => o.Source).ToHashSet();
        return levels.Contains(row.Level) && row.Facts.Findings.Any(f => sources.Contains(f.Source));
    }

    private void ShowDetails(PrivilegedAccountRowViewModel? row)
    {
        var previous = SelectedFinding?.Finding;
        if (row is null || _run is not { } run)
        {
            Findings.ReplaceAll([]);
            SelectedFinding = null;
            DetailsTitle = Localizer[L.PrivilegedAccess.NoSelection];
            return;
        }

        Findings.ReplaceAll(row.Facts.Findings.Select(f => new PrivilegeFindingViewModel(f, run.Formatter)));
        SelectedFinding = Findings.FirstOrDefault(f => previous is not null && f.Finding == previous)
            ?? Findings.FirstOrDefault(f => f.CanRemoveFromGroup)
            ?? Findings.FirstOrDefault();
        DetailsTitle = Localizer.Format(L.PrivilegedAccess.DetailsTitle, row.Name);
    }

    private void UpdateCounts()
    {
        foreach (var tile in LevelTiles)
        {
            tile.Count = _rows.Values.Count(r => r.Level == tile.Level);
            tile.CountText = IsRunning
                ? $"{tile.Count.ToString("N0", Localizer.Culture)}…"
                : tile.Count.ToString("N0", Localizer.Culture);
        }

        foreach (var option in SourceOptions)
        {
            option.Count = _rows.Values.Count(r => r.Facts.Findings.Any(f => f.Source == option.Source));
        }

        AccountCount = _rows.Count;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void RebuildItems()
    {
        var selected = Selection.Select(r => r.Dn).ToHashSet(StringComparer.Ordinal);
        var compare = Localizer.Culture.CompareInfo;
        var rows = _rows.Values.Where(PassesFilters).ToList();
        var column = _sortColumn;
        var keys = rows.ToDictionary(r => r, r => r.SortKey(column));
        rows.Sort((a, b) =>
        {
            var x = keys[a];
            var y = keys[b];
            var result = x is string sx && y is string sy
                ? compare.Compare(sx, sy, CompareOptions.IgnoreCase)
                : Comparer<object>.Default.Compare(x, y);
            if (_sortDescending)
            {
                result = -result;
            }

            return result != 0 ? result : compare.Compare(a.Name, b.Name, CompareOptions.IgnoreCase);
        });
        Items.ReplaceAll(rows);
        SelectedItem = Items.FirstOrDefault(r => selected.Contains(r.Dn));
        OnPropertyChanged(nameof(IsEmpty));
        NotifySelectionChanged();
    }

    private void RefreshCommandStates()
    {
        OpenCommand.NotifyCanExecuteChanged();
        BatchEditCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        RemoveFromGroupCommand.NotifyCanExecuteChanged();
        ClearAdminCountCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        SendMessageCommand.NotifyCanExecuteChanged();
        MarkGroupCommand.NotifyCanExecuteChanged();
        MarkAccountCommand.NotifyCanExecuteChanged();
    }

    private void UpdateStatus(bool stopped = false)
    {
        if (!HasRun || AsOf is not { } asOf)
        {
            StatusText = string.Empty;
            return;
        }

        var time = TimeZoneInfo.ConvertTime(asOf, _services.Time.LocalTimeZone).ToString("t", Localizer.Culture);
        var key = IsRunning ? L.PrivilegedAccess.StatusRunning : stopped ? L.PrivilegedAccess.StatusStopped : L.PrivilegedAccess.Status;
        StatusText = Localizer.Format(key, time, _readCount.ToString("N0", Localizer.Culture), _rows.Count.ToString("N0", Localizer.Culture));
    }

    /// <summary>Everything a run's evaluation needs (also to re-evaluate rows after changes).</summary>
    private sealed class RunContext(
        IDirectorySession session,
        PrivilegedAccessScanner scanner,
        PrivilegeEvaluator evaluator,
        ReviewScope scope,
        PrivilegedAccessFormatter formatter)
    {
        public IDirectorySession Session { get; } = session;

        public PrivilegedAccessScanner Scanner { get; } = scanner;

        public PrivilegeEvaluator Evaluator { get; } = evaluator;

        public ReviewScope Scope { get; } = scope;

        public PrivilegedAccessFormatter Formatter { get; set; } = formatter;
    }
}
