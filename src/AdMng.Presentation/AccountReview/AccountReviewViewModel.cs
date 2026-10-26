using System.Collections;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.AccountReview;

/// <summary>
/// "Przegląd kont" (README U2, design docs/dev/u2-account-review.md): one paged query over the user accounts of a scope,
/// streamed and evaluated on the client (constructed attributes cannot be filtered), tiles with the number of findings
/// per check and the list of the selected check with its own columns. From every list: properties, batch editor, CSV
/// export and the actions of the check - always through the change plan. Results exist only in memory; the parameters,
/// enabled checks, scope and named check sets are remembered in the settings.
/// </summary>
public sealed partial class AccountReviewViewModel : WindowViewModelBase
{
    /// <summary>"Przedłuż ważność…" pre-fills the end of the day this many days from today (editable in the batch editor).</summary>
    public const int ExtendByDays = 90;

    private const int FirstBatchSize = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(150);

    private readonly ViewLibrary _views;
    private readonly ICsvCodec _csv;
    private readonly DomainControllerReader _domainControllers;
    private readonly IUserMessageLauncher? _messages;
    private readonly SerialCancellation _runs = new();
    private readonly SerialCancellation _exactReads = new();
    private readonly Debouncer _refreshDebouncer;
    private readonly Dictionary<string, AccountReviewRowViewModel> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingRefresh = new(StringComparer.Ordinal);
    private RunContext? _run;
    private Task _currentRun = Task.CompletedTask;
    private Task _refreshing = Task.CompletedTask;
    private int _generation;
    private int _readCount;
    private IList? _selectedItems;
    private string _sortColumn = AccountReviewColumns.Name;
    private bool _sortDescending;
    private bool _applyingCheckSet;

    public AccountReviewViewModel(
        DialogServices services,
        ViewLibrary views,
        ICsvCodec csv,
        DomainControllerReader domainControllers,
        IUserMessageLauncher? messages = null)
        : base(services)
    {
        _views = views ?? throw new ArgumentNullException(nameof(views));
        _csv = csv ?? throw new ArgumentNullException(nameof(csv));
        _domainControllers = domainControllers ?? throw new ArgumentNullException(nameof(domainControllers));
        _messages = messages;
        _refreshDebouncer = new Debouncer(services.Time, TimeSpan.FromMilliseconds(300));
        var settings = services.Settings.Current.AccountReview;
        CheckOptions = [.. Enum.GetValues<AccountCheck>().Select(c => new AccountCheckOptionViewModel(c) { IsEnabled = !settings.DisabledChecks.Contains(c) })];
        foreach (var option in CheckOptions)
        {
            option.PropertyChanged += (_, _) => RunCommand.NotifyCanExecuteChanged();
        }

        ScopeKinds = CreateScopeKinds();
        ScopeKind = ScopeKinds[0];
        SavedViews = CreateSavedViews();
        LoadParameters(settings.Parameters);
        LoadScope(settings.Scope);
        RefreshCheckSets();
        RefreshTexts();
        Services.Changes.Changed += OnDirectoryChanged;
        _views.Changed += OnViewsChanged;
    }

    public override string WindowTitle => Session is { } session
        ? Localizer.Format(L.AccountReview.WindowTitleDomain, session.Info.DomainDnsName)
        : Localizer[L.AccountReview.WindowTitle];

    // ---- Scope ---------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial IReadOnlyList<Option<AccountReviewScopeKind>> ScopeKinds { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeText), nameof(IsViewScope), nameof(NeedsScopeTarget))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(BrowseScopeCommand))]
    public partial Option<AccountReviewScopeKind> ScopeKind { get; set; }

    /// <summary>The OU or the group of the scope (null for the domain and saved views).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeText))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? ScopeDn { get; set; }

    /// <summary>The operator's saved views (scope "saved view").</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Option<string>> SavedViews { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial Option<string>? SavedView { get; set; }

    public bool IsViewScope => ScopeKind.Value == AccountReviewScopeKind.SavedView;

    /// <summary>The scope needs an OU or a group chosen with "Wybierz…".</summary>
    public bool NeedsScopeTarget => ScopeKind.Value is AccountReviewScopeKind.OrganizationalUnit or AccountReviewScopeKind.GroupMembers;

    /// <summary>What the scope names (domain, path of the OU, name and path of the group).</summary>
    public string ScopeText => ScopeKind.Value switch
    {
        AccountReviewScopeKind.Domain => Session?.Info.DomainDnsName ?? string.Empty,
        AccountReviewScopeKind.OrganizationalUnit or AccountReviewScopeKind.GroupMembers => ScopeDn is { } dn
            ? AddressResolver.ToDisplayPath(dn)
            : Localizer[L.AccountReview.ScopeNotChosen],
        _ => string.Empty,
    };

    // ---- Parameters and checks -----------------------------------------------------------------------------------

    /// <summary>"Hasło wygasa w ≤ N dni".</summary>
    [ObservableProperty]
    public partial int PasswordExpiringDays { get; set; }

    /// <summary>"Konto wygasa w ≤ N dni".</summary>
    [ObservableProperty]
    public partial int AccountExpiringDays { get; set; }

    /// <summary>"Nieaktywne &gt; N dni" (also the creation grace of "never logged on").</summary>
    [ObservableProperty]
    public partial int InactiveDays { get; set; }

    /// <summary>"Hasło starsze niż N dni".</summary>
    [ObservableProperty]
    public partial int PasswordAgeDays { get; set; }

    /// <summary>All checks with "run it" switches.</summary>
    public IReadOnlyList<AccountCheckOptionViewModel> CheckOptions { get; }

    /// <summary>Named check sets of the settings (id, name).</summary>
    public BulkObservableCollection<Option<string>> CheckSets { get; } = [];

    /// <summary>Selecting a set applies its checks, parameters and scope.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCheckSetCommand))]
    public partial Option<string>? SelectedCheckSet { get; set; }

    /// <summary>Name for "Zapisz zestaw".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCheckSetCommand))]
    public partial string CheckSetName { get; set; } = string.Empty;

    // ---- Run -----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand), nameof(ReadExactLastLogonCommand))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsRunning { get; private set; }

    /// <summary>"Dokładne ostatnie logowanie" reads the domain controllers.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(ReadExactLastLogonCommand))]
    public partial bool IsReadingDomainControllers { get; private set; }

    /// <summary>A run was started (the tiles are shown).</summary>
    [ObservableProperty]
    public partial bool HasRun { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>"Stan na godz. 14:32 · 1 234 kont odczytanych · 56 kont z uwagami".</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>Reference time of the last run.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? AsOf { get; private set; }

    /// <summary>The checks of the last run with their counts.</summary>
    public BulkObservableCollection<AccountCheckTileViewModel> Tiles { get; } = [];

    [ObservableProperty]
    public partial AccountCheckTileViewModel? SelectedTile { get; set; }

    /// <summary>The check whose list is shown.</summary>
    public AccountCheck? SelectedCheck => SelectedTile?.Check;

    /// <summary>Columns of the list of <see cref="SelectedCheck"/>.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AccountReviewColumnViewModel> Columns { get; private set; } = [];

    /// <summary>The accounts of the selected check, sorted.</summary>
    public BulkObservableCollection<AccountReviewRowViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial AccountReviewRowViewModel? SelectedItem { get; set; }

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
    public IReadOnlyList<AccountReviewRowViewModel> Selection =>
        _selectedItems is { Count: > 0 } items ? [.. items.OfType<AccountReviewRowViewModel>()]
        : SelectedItem is { } row ? [row]
        : [];

    [ObservableProperty]
    public partial string SelectionText { get; private set; } = string.Empty;

    /// <summary>What to keep in mind about the selected check (e.g. the replication delay of lastLogonTimestamp).</summary>
    [ObservableProperty]
    public partial string ListHint { get; private set; } = string.Empty;

    public bool IsEmpty => HasRun && !IsRunning && SelectedTile is not null && Items.Count == 0;

    /// <summary>The last run (also a stopped one).</summary>
    public Task Running => _currentRun;

    /// <summary>The pending refresh of rows changed through the pipeline.</summary>
    public Task Refreshing => _refreshing;

    // ---- Actions of the selected check (visibility) --------------------------------------------------------------

    public bool ShowUnlock => SelectedCheck is AccountCheck.Locked;

    public bool ShowRequirePasswordChange => SelectedCheck is AccountCheck.PasswordExpired or AccountCheck.PasswordExpiring
        or AccountCheck.PasswordNotRequired or AccountCheck.ReversibleEncryption or AccountCheck.PasswordOld;

    public bool ShowExtend => SelectedCheck is AccountCheck.AccountExpired or AccountCheck.AccountExpiring;

    public bool ShowDisable => SelectedCheck is AccountCheck.Inactive or AccountCheck.NeverLoggedOn;

    public bool ShowExactLastLogon => SelectedCheck is AccountCheck.Inactive or AccountCheck.NeverLoggedOn;

    public bool ShowClearFlag => SelectedCheck is AccountCheck.PasswordNeverExpires or AccountCheck.PasswordNotRequired or AccountCheck.ReversibleEncryption;

    public bool ShowRemoveFromGroups => SelectedCheck is AccountCheck.DisabledInGroups;

    /// <summary>"Ukryj w GAL" needs Exchange's msExchHideFromAddressLists in the schema.</summary>
    public bool ShowHideFromGal => SelectedCheck is AccountCheck.DisabledInGal
        && _run?.Session.Schema.FindAttribute(Attr.HideFromAddressLists) is not null;

    /// <summary>
    /// U1 hook: "Wyślij wiadomość…" is shown only when an <see cref="IUserMessageLauncher"/> is registered (U1 is not
    /// implemented yet, so it stays hidden).
    /// </summary>
    public bool CanSendMessages => _messages is not null;

    // ---- Commands ------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAsync() => _currentRun = RunCoreAsync();

    private bool CanRun() => !IsRunning && !IsSessionEnded && Session is not null && CheckOptions.Any(o => o.IsEnabled) && ScopeKind.Value switch
    {
        AccountReviewScopeKind.OrganizationalUnit or AccountReviewScopeKind.GroupMembers => ScopeDn is not null,
        AccountReviewScopeKind.SavedView => SavedView is not null,
        _ => true,
    };

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _runs.Cancel();
        _exactReads.Cancel();
    }

    private bool CanStop() => IsRunning || IsReadingDomainControllers;

    [RelayCommand(CanExecute = nameof(CanBrowseScope))]
    private async Task BrowseScopeAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        if (ScopeKind.Value == AccountReviewScopeKind.OrganizationalUnit)
        {
            if (await Services.Dialogs.PickContainerAsync(session, ScopeDn).ConfigureAwait(true) is { } container)
            {
                ScopeDn = container;
            }
        }
        else if (ScopeKind.Value == AccountReviewScopeKind.GroupMembers)
        {
            var picked = await Services.Dialogs.PickObjectsAsync(session, [ObjectKind.Group], multiSelect: false).ConfigureAwait(true);
            if (picked.Count > 0)
            {
                ScopeDn = picked[0];
            }
        }
    }

    private bool CanBrowseScope() => NeedsScopeTarget && !IsSessionEnded;

    [RelayCommand(CanExecute = nameof(CanSaveCheckSet))]
    private async Task SaveCheckSetAsync()
    {
        var name = CheckSetName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        var existing = Services.Settings.Current.AccountReview.CheckSets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase));
        var set = new AccountReviewCheckSet
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = name,
            Checks = EnabledChecks().Order().ToList(),
            Parameters = CurrentParameters(),
            Scope = CurrentScope(),
        };
        await Services.Settings.UpdateAsync(s =>
        {
            var index = s.AccountReview.CheckSets.FindIndex(c => c.Id == set.Id);
            if (index >= 0)
            {
                s.AccountReview.CheckSets[index] = set;
            }
            else if (s.AccountReview.CheckSets.Count < AccountReviewSettings.MaxCheckSets)
            {
                s.AccountReview.CheckSets.Add(set);
            }
        }).ConfigureAwait(true);
        RefreshCheckSets(select: set.Id);
        Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.AccountReview.CheckSetSaved, name));
    }

    private bool CanSaveCheckSet() => CheckSetName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanDeleteCheckSet))]
    private async Task DeleteCheckSetAsync()
    {
        if (SelectedCheckSet is not { } selected)
        {
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync(
                Localizer[L.AccountReview.DeleteCheckSet],
                Localizer.Format(L.AccountReview.DeleteCheckSetConfirm, selected.Text),
                destructive: true).ConfigureAwait(true))
        {
            return;
        }

        await Services.Settings.UpdateAsync(s => s.AccountReview.CheckSets.RemoveAll(c => c.Id == selected.Value)).ConfigureAwait(true);
        RefreshCheckSets();
    }

    private bool CanDeleteCheckSet() => SelectedCheckSet is not null;

    [RelayCommand]
    private void Sort(string? column)
    {
        if (string.IsNullOrEmpty(column))
        {
            return;
        }

        _sortDescending = column == _sortColumn && !_sortDescending;
        _sortColumn = column;
        RebuildItems();
    }

    /// <summary>Opens the properties of a row (double click / Enter); without a row the selected one.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync(AccountReviewRowViewModel? row)
    {
        row ??= SelectedItem;
        return row is null || IsSessionEnded ? Task.CompletedTask : Services.Dialogs.ShowPropertiesAsync(row.Dn);
    }

    private bool CanOpen(AccountReviewRowViewModel? row) => !IsSessionEnded;

    /// <summary>Hands the selection (or the whole list) over to the batch editor.</summary>
    [RelayCommand(CanExecute = nameof(HasRows))]
    private Task BatchEditAsync()
    {
        var rows = Targets();
        return rows.Count == 0 || Session is null
            ? Task.CompletedTask
            : Services.Dialogs.ShowBatchEditorAsync(new BatchEditorRequest(Dns(rows)) { Title = Localizer[L.Commands.BatchEdit] });
    }

    /// <summary>Exports the list of the selected check (all rows, in the shown order) to a CSV file the operator chooses.</summary>
    [RelayCommand(CanExecute = nameof(HasItems))]
    private async Task ExportCsvAsync()
    {
        if (SelectedTile is not { } tile || Items.Count == 0)
        {
            return;
        }

        var title = Localizer[L.AccountReview.ExportCsv];
        var path = await Services.Files.SaveFileAsync(
            title,
            ListExportSnapshot.SuggestFileName(tile.Title, Services.Time.GetLocalNow()),
            [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var columns = Columns.Select(c => c.Id).Append(AccountReviewColumns.DistinguishedName).ToList();
        var headers = columns.Select(c => Localizer[AccountReviewFormatter.ColumnKey(c)]).ToList();
        var rows = Items.Select(r => (IReadOnlyList<string>)[.. columns.Select(c => r[c])]).ToList();
        var delimiter = CsvFiles.ListSeparator(Services.Regional.Culture);
        try
        {
            await CsvFiles.WriteAsync(path, writer => _csv.Write(writer, headers, rows, delimiter, protectFormulas: true)).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Files.Exported, rows.Count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectionForChange))]
    private Task UnlockAsync() => ApplyAsync(Selection, new UnlockAccountOperation(), Localizer[L.Commands.Unlock]);

    [RelayCommand(CanExecute = nameof(HasSelectionForChange))]
    private Task RequirePasswordChangeAsync() =>
        ApplyAsync(Selection, new RequirePasswordChangeOperation(true), Localizer[L.AccountReview.RequirePasswordChange]);

    [RelayCommand(CanExecute = nameof(HasSelectionForChange))]
    private Task DisableAsync() => ApplyAsync(Selection, new SetAccountEnabledOperation(false), Localizer[L.Commands.Disable]);

    /// <summary>"Przedłuż ważność…": the batch editor with the account expiration pre-filled (the operator sets the date).</summary>
    [RelayCommand(CanExecute = nameof(HasRowsForChange))]
    private Task ExtendAsync()
    {
        var rows = Targets();
        if (rows.Count == 0 || Session is null)
        {
            return Task.CompletedTask;
        }

        var zone = Services.Time.LocalTimeZone;
        var day = AccountStatus.LocalDate(Services.Time.GetUtcNow(), zone).AddDays(ExtendByDays + 1).ToDateTime(TimeOnly.MinValue);
        var midnight = new DateTimeOffset(day, zone.GetUtcOffset(day));
        return Services.Dialogs.ShowBatchEditorAsync(new BatchEditorRequest(Dns(rows))
        {
            Operations = [new SetAccountExpirationOperation(midnight)],
            Title = Localizer[L.AccountReview.Extend],
        });
    }

    /// <summary>Clears the hygiene flag of the selected check (batch preview).</summary>
    [RelayCommand(CanExecute = nameof(HasRowsForChange))]
    private Task ClearFlagAsync()
    {
        UserAccountControl? flag = SelectedCheck switch
        {
            AccountCheck.PasswordNeverExpires => UserAccountControl.DontExpirePassword,
            AccountCheck.PasswordNotRequired => UserAccountControl.PasswordNotRequired,
            AccountCheck.ReversibleEncryption => UserAccountControl.EncryptedTextPasswordAllowed,
            _ => null,
        };
        return flag is { } f
            ? ApplyAsync(Targets(), new SetAccountFlagOperation(f, false), Localizer[L.AccountReview.ClearFlag], preview: true)
            : Task.CompletedTask;
    }

    /// <summary>Removes disabled accounts from all their groups (batch preview; the primary group stays).</summary>
    [RelayCommand(CanExecute = nameof(HasRowsForChange))]
    private Task RemoveFromGroupsAsync()
    {
        var rows = Targets();
        var groups = rows.SelectMany(r => r.Facts.Groups).DistinctBy(DnKey.For, StringComparer.Ordinal).ToList();
        return groups.Count == 0
            ? Task.CompletedTask
            : ApplyAsync(rows, new RemoveFromGroupsOperation(groups), Localizer[L.AccountReview.RemoveFromGroups], preview: true);
    }

    [RelayCommand(CanExecute = nameof(HasRowsForChange))]
    private Task HideFromGalAsync() =>
        ApplyAsync(Targets(), new SetAttributeOperation(Attr.HideFromAddressLists, "TRUE"), Localizer[L.AccountReview.HideFromGal], preview: true);

    /// <summary>
    /// "Dokładne ostatnie logowanie (wszystkie DC)": lastLogon of the selection (or the list) on every DC; accounts that
    /// are active after all leave the inactivity lists.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReadExactLastLogon))]
    private async Task ReadExactLastLogonAsync()
    {
        var rows = Targets();
        if (rows.Count == 0 || Session is not { } session || _run is not { } run)
        {
            return;
        }

        var token = _exactReads.Next();
        IsReadingDomainControllers = true;
        StatusText = Localizer.Format(L.AccountReview.ReadingDomainControllers, rows.Count);
        try
        {
            var dns = Dns(rows);
            var reads = await Task.Run(() => _domainControllers.ReadAsync(session, dns, [Attr.LastLogon], token), token).ConfigureAwait(true);
            if (!ReferenceEquals(run, _run))
            {
                return;
            }

            var answered = reads.Where(r => r.Succeeded).ToList();
            var active = 0;
            foreach (var row in rows)
            {
                var best = answered
                    .Select(r => (r.Server, Time: r.Find(row.Dn)?.GetFileTime(Attr.LastLogon)))
                    .Where(v => v.Time is not null)
                    .OrderByDescending(v => v.Time)
                    .FirstOrDefault();
                var facts = row.Facts with
                {
                    ExactLastLogon = best.Time,
                    ExactLastLogonServer = best.Time is null ? null : best.Server,
                    HasExactLastLogon = true,
                    Status = row.Facts.Status.WithExactLastLogon(best.Time),
                };
                var before = row.Checks.Contains(AccountCheck.Inactive) || row.Checks.Contains(AccountCheck.NeverLoggedOn);
                Place(run, facts);
                var after = _rows.TryGetValue(DnKey.For(facts.DistinguishedName), out var current)
                    && (current.Checks.Contains(AccountCheck.Inactive) || current.Checks.Contains(AccountCheck.NeverLoggedOn));
                if (before && !after)
                {
                    active++;
                }
            }

            UpdateTiles(complete: true);
            RebuildItems();
            var failed = reads.Where(r => !r.Succeeded).ToList();
            var message = Localizer.Format(L.AccountReview.ExactDone, answered.Count, reads.Count, active);
            if (failed.Count > 0)
            {
                message += Environment.NewLine + string.Join(Environment.NewLine, failed.Select(f => $"{f.Server}: {f.Error}"));
            }

            Services.Notifications.Show(failed.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success, message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopped by the operator or a new run.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Services.Dialogs.ShowErrorAsync(Localizer[L.AccountReview.ExactLastLogon], Errors.Translate(ex)).ConfigureAwait(true);
        }
        finally
        {
            IsReadingDomainControllers = false;
            UpdateStatus();
        }
    }

    private bool CanReadExactLastLogon() => ShowExactLastLogon && !IsRunning && !IsReadingDomainControllers && !IsSessionEnded && Items.Count > 0;

    /// <summary>U1 hook (hidden while no <see cref="IUserMessageLauncher"/> is registered).</summary>
    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private Task SendMessageAsync()
    {
        var rows = Targets();
        return _messages is null || rows.Count == 0 ? Task.CompletedTask : _messages.ShowComposerAsync(Dns(rows), SelectedCheck);
    }

    private bool CanSendMessage() => CanSendMessages && HasRows();

    [RelayCommand]
    private void Close() => RequestClose(true);

    private bool HasRows() => Items.Count > 0 && !IsSessionEnded;

    private bool HasItems() => Items.Count > 0;

    private bool HasRowsForChange() => Items.Count > 0 && !IsSessionEnded && !IsRunning;

    private bool HasSelectionForChange() => Selection.Count > 0 && !IsSessionEnded && !IsRunning;

    /// <summary>Called by the view when the list's selection changed.</summary>
    public void NotifySelectionChanged()
    {
        var count = Selection.Count;
        SelectionText = count > 1 ? Localizer.Format(L.AccountReview.Selected, count) : string.Empty;
        RefreshCommandStates();
    }

    // ---- Lifetime --------------------------------------------------------------------------------------------------

    public override Task InitializeAsync()
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(ScopeText));
        RunCommand.NotifyCanExecuteChanged();
        return Task.CompletedTask;
    }

    protected override void OnSessionEnded()
    {
        _runs.Cancel();
        _exactReads.Cancel();
        _refreshDebouncer.Cancel();
        RunCommand.NotifyCanExecuteChanged();
        BrowseScopeCommand.NotifyCanExecuteChanged();
        RefreshCommandStates();
        OnPropertyChanged(nameof(WindowTitle));
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        var kind = ScopeKind.Value;
        ScopeKinds = CreateScopeKinds();
        ScopeKind = ScopeKinds.First(k => k.Value == kind);
        RefreshTexts();
        if (_run is { } run)
        {
            var formatter = new AccountReviewFormatter(Localizer, run.TimeZone, run.Parameters);
            run.Formatter = formatter;
            foreach (var row in _rows.Values)
            {
                row.UseFormatter(formatter);
            }
        }

        UpdateTiles(complete: !IsRunning);
        UpdateStatus();
        NotifySelectionChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runs.Cancel();
            _exactReads.Cancel();
            _refreshDebouncer.Cancel();
            Services.Changes.Changed -= OnDirectoryChanged;
            _views.Changed -= OnViewsChanged;
        }
    }

    partial void OnScopeKindChanged(Option<AccountReviewScopeKind> oldValue, Option<AccountReviewScopeKind> newValue)
    {
        if (oldValue is not null && oldValue.Value != newValue.Value)
        {
            ScopeDn = null;
        }
    }

    partial void OnSelectedCheckSetChanged(Option<string>? value)
    {
        if (value is null || _applyingCheckSet)
        {
            return;
        }

        if (Services.Settings.Current.AccountReview.CheckSets.FirstOrDefault(s => s.Id == value.Value) is { } set)
        {
            foreach (var option in CheckOptions)
            {
                option.IsEnabled = set.Checks.Contains(option.Check);
            }

            LoadParameters(set.Parameters);
            LoadScope(set.Scope);
            CheckSetName = set.Name;
        }
    }

    partial void OnSelectedItemChanged(AccountReviewRowViewModel? value) => NotifySelectionChanged();

    partial void OnSelectedTileChanged(AccountCheckTileViewModel? value)
    {
        foreach (var tile in Tiles)
        {
            tile.IsSelected = ReferenceEquals(tile, value);
        }

        Columns = value is null
            ? []
            : [.. AccountReviewColumns.For(value.Check).Select(c => new AccountReviewColumnViewModel(c, Localizer[AccountReviewFormatter.ColumnKey(c)]))];
        ListHint = value is null ? string.Empty : Localizer[AccountReviewFormatter.CheckHintKey(value.Check)];
        OnPropertyChanged(nameof(SelectedCheck));
        OnPropertyChanged(nameof(ShowUnlock));
        OnPropertyChanged(nameof(ShowRequirePasswordChange));
        OnPropertyChanged(nameof(ShowExtend));
        OnPropertyChanged(nameof(ShowDisable));
        OnPropertyChanged(nameof(ShowExactLastLogon));
        OnPropertyChanged(nameof(ShowClearFlag));
        OnPropertyChanged(nameof(ShowRemoveFromGroups));
        OnPropertyChanged(nameof(ShowHideFromGal));
        RebuildItems();
    }

    // ---- Run -------------------------------------------------------------------------------------------------------

    private async Task RunCoreAsync()
    {
        var token = _runs.Next();
        _exactReads.Cancel();
        var generation = ++_generation;
        _rows.Clear();
        _pendingRefresh.Clear();
        _readCount = 0;
        Items.ReplaceAll([]);
        Error = null;
        var session = Session;
        if (session is null)
        {
            Error = NoSessionMessage;
            return;
        }

        var checks = EnabledChecks();
        var parameters = CurrentParameters();
        LoadParameters(parameters);
        await RememberAsync(parameters).ConfigureAwait(true);
        var time = Services.Time;
        var now = time.GetUtcNow();
        var zone = time.LocalTimeZone;
        var previous = SelectedTile?.Check;
        Tiles.ReplaceAll(checks.Order().Select(c => new AccountCheckTileViewModel(c)));
        RefreshTexts();
        UpdateTiles(complete: false);
        SelectedTile = Tiles.FirstOrDefault(t => t.Check == previous) ?? Tiles.FirstOrDefault();
        HasRun = true;
        AsOf = now;
        IsRunning = true;
        var stopped = false;
        try
        {
            var policy = await ReadPolicyAsync(token).ConfigureAwait(true);
            var request = new ScopeRequest(
                ScopeKind.Value,
                ScopeDn,
                SavedView is { } option ? _views.FindView(option.Value) : null,
                Services.Settings.Current.InactiveDays);
            var target = await Task.Run(() => ResolveScopeAsync(session, request, policy, now, token), token).ConfigureAwait(true);
            IReadOnlySet<string>? privileged = checks.Contains(AccountCheck.PrivilegedWithProblem)
                ? await Task.Run(() => ReadPrivilegedAsync(session, target, policy, token), token).ConfigureAwait(true)
                : null;
            var run = new RunContext(
                session, now, zone, parameters, checks, policy, privileged,
                new AccountReviewFormatter(Localizer, zone, parameters), AccountReviewQuery.Attributes(session.Schema));
            _run = run;
            var search = new DirectorySearch
            {
                BaseDn = target.BaseDn,
                Scope = target.Scope,
                Filter = AccountReviewQuery.Filter(checks, target.Filter, parameters, now, session.Schema),
                Attributes = run.Attributes,
                PageSize = Services.Settings.Current.PageSize,
            };
            var candidates = await StreamAsync(run, search, generation, token).ConfigureAwait(true);
            if (checks.Contains(AccountCheck.ManagerDisabled) && generation == _generation)
            {
                var resolved = await Task.Run(() => ResolveManagersAsync(run, candidates, token), token).ConfigureAwait(true);
                if (generation == _generation)
                {
                    foreach (var facts in resolved)
                    {
                        Place(run, facts);
                    }
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
                Error = ex is ScopeException scope ? scope.Message : Errors.Translate(ex);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsRunning = false;
                UpdateTiles(complete: !stopped);
                RebuildItems();
                UpdateStatus(stopped);
                OnPropertyChanged(nameof(ShowHideFromGal));
            }
        }
    }

    private async Task<DomainPolicy> ReadPolicyAsync(CancellationToken token)
    {
        try
        {
            return await Services.Sessions.GetDomainPolicyAsync(token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException)
        {
            return DomainPolicy.Unknown;
        }
    }

    /// <summary>Base, scope and filter of the review query.</summary>
    private async Task<ScopeTarget> ResolveScopeAsync(IDirectorySession session, ScopeRequest request, DomainPolicy policy, DateTimeOffset now, CancellationToken token)
    {
        var nc = session.Info.DefaultNamingContext;
        switch (request.Kind)
        {
            case AccountReviewScopeKind.OrganizationalUnit:
                return new ScopeTarget(request.Dn ?? throw new ScopeException(Localizer[L.AccountReview.ScopeNotChosen]), SearchScope.Subtree, null);
            case AccountReviewScopeKind.SavedView:
                var view = request.View ?? throw new ScopeException(Localizer[L.AccountReview.ViewNotFound]);
                var filter = QuickFilters.Combine(LdapFilter.Wrap(view.Filter), view.QuickFilters ?? [], now, request.InactiveDays, policy.DomainSid);
                return new ScopeTarget(view.ResolveBaseDn(nc), view.Scope, filter);
            case AccountReviewScopeKind.GroupMembers:
                var groupDn = request.Dn ?? throw new ScopeException(Localizer[L.AccountReview.ScopeNotChosen]);
                var group = await session.GetAsync(groupDn, [Attr.ObjectSid], token).ConfigureAwait(false)
                    ?? throw new ScopeException(Localizer.Format(L.AccountReview.GroupNotFound, AddressResolver.ToDisplayPath(groupDn)));
                return new ScopeTarget(nc, SearchScope.Subtree, AccountReviewQuery.GroupMembersFilter(group.DistinguishedName, AdConvert.GetRid(group.ObjectSid)));
            default:
                return new ScopeTarget(nc, SearchScope.Subtree, null);
        }
    }

    /// <summary>DnKeys of the accounts in scope with adminCount = 1 or a nested membership of a protected group.</summary>
    private static async Task<IReadOnlySet<string>> ReadPrivilegedAsync(IDirectorySession session, ScopeTarget target, DomainPolicy policy, CancellationToken token)
    {
        var groups = new List<string>();
        var groupSearch = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = AccountReviewQuery.ProtectedGroupsFilter(policy.DomainSid),
            Attributes = [],
        };
        await foreach (var group in session.SearchAsync(groupSearch, token).ConfigureAwait(false))
        {
            groups.Add(group.DistinguishedName);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        var search = new DirectorySearch
        {
            BaseDn = target.BaseDn,
            Scope = target.Scope,
            Filter = LdapFilter.And(QuickFilters.Users, target.Filter, AccountReviewQuery.PrivilegedFilter(groups)),
            Attributes = [],
        };
        await foreach (var account in session.SearchAsync(search, token).ConfigureAwait(false))
        {
            result.Add(DnKey.For(account.DistinguishedName));
        }

        return result;
    }

    /// <summary>
    /// Streams the review query: evaluated off the UI thread, findings handed over in batches. Returns the enabled accounts
    /// with a manager whose only possible finding is "manager disabled" (resolved after the stream).
    /// </summary>
    private Task<List<AccountReviewFacts>> StreamAsync(RunContext run, DirectorySearch search, int generation, CancellationToken token)
    {
        var time = Services.Time;
        return Task.Run(
            async () =>
            {
                var candidates = new List<AccountReviewFacts>();
                var buffer = new List<AccountReviewFacts>(FirstBatchSize);
                var read = 0;
                var lastFlush = time.GetTimestamp();
                try
                {
                    await foreach (var obj in run.Session.SearchAsync(search, token).WithCancellation(token).ConfigureAwait(false))
                    {
                        read++;
                        var facts = run.Evaluate(obj);
                        if (run.Checks.Contains(AccountCheck.ManagerDisabled) && facts.ManagerDn is not null && !facts.Status.IsDisabled)
                        {
                            candidates.Add(facts);
                        }

                        buffer.Add(facts);
                        if (buffer.Count >= FirstBatchSize || time.GetElapsedTime(lastFlush) >= FlushInterval)
                        {
                            await FlushAsync(run, buffer, read, generation).ConfigureAwait(false);
                            buffer = new List<AccountReviewFacts>(FirstBatchSize);
                            lastFlush = time.GetTimestamp();
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Stopped: what was read so far stays visible (the tiles say the counts are not final).
                    await FlushAsync(run, buffer, read, generation).ConfigureAwait(false);
                    throw;
                }

                await FlushAsync(run, buffer, read, generation).ConfigureAwait(false);
                return candidates;
            },
            token);
    }

    private Task FlushAsync(RunContext run, List<AccountReviewFacts> batch, int read, int generation) => Services.Dispatcher.InvokeAsync(() =>
    {
        if (generation != _generation || IsDisposed)
        {
            return;
        }

        _readCount = read;
        var added = new List<AccountReviewRowViewModel>();
        foreach (var facts in batch)
        {
            if (Place(run, facts) is { } row && SelectedCheck is { } check && row.Checks.Contains(check))
            {
                added.Add(row);
            }
        }

        Items.AddRange(added);
        UpdateTiles(complete: false);
        UpdateStatus();
    });

    /// <summary>Managers of the candidates: from the findings when they are listed, else read in batches (UAC only).</summary>
    private async Task<List<AccountReviewFacts>> ResolveManagersAsync(RunContext run, List<AccountReviewFacts> candidates, CancellationToken token)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var managers = await DirectoryLookup.ReadManyAsync(
            run.Session, candidates.Select(c => c.ManagerDn!), [Attr.UserAccountControl], token).ConfigureAwait(false);
        return [.. candidates.Select(c => c with
        {
            IsManagerDisabled = managers.TryGetValue(DnKey.For(c.ManagerDn!), out var manager) ? manager.IsDisabled : null,
        })];
    }

    /// <summary>
    /// Adds, updates or removes the row of an evaluated account. Returns the row when it was added (a new finding); the
    /// caller puts it into <see cref="Items"/> or rebuilds the list.
    /// </summary>
    private AccountReviewRowViewModel? Place(RunContext run, AccountReviewFacts facts)
    {
        var key = DnKey.For(facts.DistinguishedName);
        if (_rows.TryGetValue(key, out var existing))
        {
            // Facts resolved later (manager, exact last logon) survive a re-read of the account.
            facts = facts with
            {
                IsManagerDisabled = facts.IsManagerDisabled ?? (DnKey.For(existing.Facts.ManagerDn ?? string.Empty) == DnKey.For(facts.ManagerDn ?? string.Empty)
                    ? existing.Facts.IsManagerDisabled
                    : null),
                ExactLastLogon = facts.HasExactLastLogon ? facts.ExactLastLogon : existing.Facts.ExactLastLogon,
                ExactLastLogonServer = facts.HasExactLastLogon ? facts.ExactLastLogonServer : existing.Facts.ExactLastLogonServer,
                HasExactLastLogon = facts.HasExactLastLogon || existing.Facts.HasExactLastLogon,
                Status = facts.HasExactLastLogon || !existing.Facts.HasExactLastLogon
                    ? facts.Status
                    : facts.Status.WithExactLastLogon(existing.Facts.ExactLastLogon),
            };
        }

        var checks = run.Checks.Where(c => AccountReviewQuery.Matches(c, facts, run.Parameters)).ToHashSet();
        if (checks.Count == 0)
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
            existing.Update(facts, checks);
            return null;
        }

        var row = new AccountReviewRowViewModel(facts, checks, run.Formatter);
        _rows[key] = row;
        return row;
    }

    // ---- Refresh after changes ---------------------------------------------------------------------------------

    /// <summary>Rows changed through the pipeline (here, in the batch editor or elsewhere) are read again (debounced).</summary>
    private void OnDirectoryChanged(object? sender, DirectoryChangesEventArgs e) => _ = Services.Dispatcher.RunAsync(() =>
    {
        if (IsDisposed || _run is null)
        {
            return;
        }

        var changed = false;
        foreach (var change in e.Changes)
        {
            var key = DnKey.For(change.DistinguishedName);
            if (!_rows.TryGetValue(key, out var row))
            {
                continue;
            }

            if (change.Kind == DirectoryChangeKind.Deleted)
            {
                _rows.Remove(key);
                Items.Remove(row);
                changed = true;
            }
            else if (change.Kind == DirectoryChangeKind.Moved && change.NewDistinguishedName is { } moved)
            {
                _rows.Remove(key);
                Items.Remove(row);
                _pendingRefresh.Add(moved);
                changed = true;
            }
            else
            {
                _pendingRefresh.Add(row.Dn);
            }
        }

        if (changed)
        {
            UpdateTiles(complete: !IsRunning);
        }

        if (_pendingRefresh.Count > 0)
        {
            _refreshing = _refreshDebouncer.RunAsync(RefreshPendingAsync);
        }
    });

    private async Task RefreshPendingAsync(CancellationToken token)
    {
        if (_run is not { } run || Session is not { } session || !ReferenceEquals(session, run.Session) || IsRunning)
        {
            _pendingRefresh.Clear();
            return;
        }

        var dns = _pendingRefresh.ToList();
        _pendingRefresh.Clear();
        try
        {
            var found = await Task.Run(() => DirectoryLookup.ReadManyAsync(session, dns, run.Attributes, token), token).ConfigureAwait(true);
            var evaluated = found.Values.Select(run.Evaluate).ToList();
            if (run.Checks.Contains(AccountCheck.ManagerDisabled))
            {
                var withManager = evaluated.Where(f => f.ManagerDn is not null).ToList();
                var resolved = await Task.Run(() => ResolveManagersAsync(run, withManager, token), token).ConfigureAwait(true);
                evaluated = [.. evaluated.Where(f => f.ManagerDn is null), .. resolved];
            }

            if (!ReferenceEquals(run, _run))
            {
                return;
            }

            foreach (var dn in dns.Where(d => !found.ContainsKey(DnKey.For(d))))
            {
                if (_rows.Remove(DnKey.For(dn), out var gone))
                {
                    Items.Remove(gone);
                }
            }

            foreach (var facts in evaluated)
            {
                Place(run, facts);
            }

            UpdateTiles(complete: true);
            RebuildItems();
            UpdateStatus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded or closed.
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            Services.Notifications.Show(NotificationSeverity.Warning, Errors.Translate(ex));
        }
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private async Task ApplyAsync(IReadOnlyList<AccountReviewRowViewModel> rows, BatchOperation operation, string title, bool preview = false)
    {
        // Only in the session the window belongs to (the coordinator uses the current one).
        if (rows.Count == 0 || Session is null)
        {
            return;
        }

        await Services.Coordinator.ApplyAsync(new ChangeRequest(Dns(rows), [operation], title) { ForcePreview = preview }).ConfigureAwait(true);
    }

    /// <summary>The selection, or the whole list when nothing is selected.</summary>
    private IReadOnlyList<AccountReviewRowViewModel> Targets() => Selection is { Count: > 0 } selection ? selection : [.. Items];

    private static IReadOnlyList<string> Dns(IEnumerable<AccountReviewRowViewModel> rows) => [.. rows.Select(r => r.Dn)];

    private HashSet<AccountCheck> EnabledChecks() => [.. CheckOptions.Where(o => o.IsEnabled).Select(o => o.Check)];

    private AccountReviewParameters CurrentParameters() => new AccountReviewParameters
    {
        ExpiryWarningDays = PasswordExpiringDays,
        AccountExpiryWarningDays = AccountExpiringDays,
        InactiveDays = InactiveDays,
        RotationDays = PasswordAgeDays,
    }.Normalized();

    private void LoadParameters(AccountReviewParameters parameters)
    {
        var normalized = parameters.Normalized();
        PasswordExpiringDays = normalized.ExpiryWarningDays;
        AccountExpiringDays = normalized.AccountExpiryWarningDays;
        InactiveDays = normalized.InactiveDays;
        PasswordAgeDays = normalized.RotationDays;
    }

    /// <summary>The scope in the portable form of the settings.</summary>
    private AccountReviewScope CurrentScope()
    {
        var nc = Session?.Info.DefaultNamingContext;
        return new AccountReviewScope
        {
            Kind = ScopeKind.Value,
            Dn = ScopeDn is { } dn && NeedsScopeTarget ? (nc is null ? dn : Views.SavedView.ToPortableDn(dn, nc)) : null,
            ViewId = IsViewScope ? SavedView?.Value : null,
        };
    }

    private void LoadScope(AccountReviewScope scope)
    {
        ScopeKind = ScopeKinds.FirstOrDefault(k => k.Value == scope.Kind) ?? ScopeKinds[0];
        var nc = Session?.Info.DefaultNamingContext;
        ScopeDn = NeedsScopeTarget && scope.Dn is { } dn ? FromPortable(dn, nc) : null;
        SavedView = IsViewScope ? SavedViews.FirstOrDefault(v => v.Value == scope.ViewId) : null;
    }

    /// <summary>A portable DN (<c>{domain}</c>) of the settings in this session's domain; null when it cannot be resolved.</summary>
    private static string? FromPortable(string dn, string? nc)
    {
        var placeholder = dn.Contains(Views.SavedView.DomainPlaceholder, StringComparison.OrdinalIgnoreCase);
        if (nc is null)
        {
            return placeholder ? null : dn;
        }

        var resolved = placeholder ? dn.Replace(Views.SavedView.DomainPlaceholder, nc, StringComparison.OrdinalIgnoreCase) : dn;
        return Core.Ldap.DistinguishedName.TryParse(resolved, out _) ? resolved : null;
    }

    private Task RememberAsync(AccountReviewParameters parameters)
    {
        var disabled = CheckOptions.Where(o => !o.IsEnabled).Select(o => o.Check).ToList();
        var scope = CurrentScope();
        return Services.Settings.UpdateAsync(s =>
        {
            s.AccountReview.Parameters = parameters;
            s.AccountReview.DisabledChecks = disabled;
            s.AccountReview.Scope = scope;
        });
    }

    private void RefreshCheckSets(string? select = null)
    {
        var current = select ?? SelectedCheckSet?.Value;
        _applyingCheckSet = true;
        try
        {
            CheckSets.ReplaceAll(Services.Settings.Current.AccountReview.CheckSets.Select(s => new Option<string>(s.Id, s.Name)));
            SelectedCheckSet = CheckSets.FirstOrDefault(s => s.Value == current);
        }
        finally
        {
            _applyingCheckSet = false;
        }
    }

    private void OnViewsChanged(object? sender, EventArgs e) => _ = Services.Dispatcher.RunAsync(() =>
    {
        var selected = SavedView?.Value;
        SavedViews = CreateSavedViews();
        SavedView = SavedViews.FirstOrDefault(v => v.Value == selected);
    });

    private List<Option<AccountReviewScopeKind>> CreateScopeKinds() =>
        [.. Enum.GetValues<AccountReviewScopeKind>().Select(k => new Option<AccountReviewScopeKind>(k, Localizer[$"AccountReview.Scope.{k}"]))];

    private List<Option<string>> CreateSavedViews() => [.. _views.SavedViews.Select(v => new Option<string>(v.Id, v.Name))];

    private void RefreshTexts()
    {
        foreach (var option in CheckOptions)
        {
            option.Title = Localizer[AccountReviewFormatter.CheckKey(option.Check)];
            option.AreaTitle = Localizer[AccountReviewFormatter.AreaKey(option.Area)];
        }

        foreach (var tile in Tiles)
        {
            tile.Title = Localizer[AccountReviewFormatter.CheckKey(tile.Check)];
            tile.AreaTitle = Localizer[AccountReviewFormatter.AreaKey(tile.Area)];
            tile.Hint = Localizer[AccountReviewFormatter.CheckHintKey(tile.Check)];
        }

        if (SelectedTile is { } selected)
        {
            Columns = [.. AccountReviewColumns.For(selected.Check).Select(c => new AccountReviewColumnViewModel(c, Localizer[AccountReviewFormatter.ColumnKey(c)]))];
            ListHint = Localizer[AccountReviewFormatter.CheckHintKey(selected.Check)];
        }

        OnPropertyChanged(nameof(ScopeText));
    }

    private void UpdateTiles(bool complete)
    {
        var counts = new Dictionary<AccountCheck, int>();
        foreach (var row in _rows.Values)
        {
            foreach (var check in row.Checks)
            {
                counts[check] = counts.GetValueOrDefault(check) + 1;
            }
        }

        foreach (var tile in Tiles)
        {
            tile.SetCount(counts.GetValueOrDefault(tile.Check), complete, Localizer.Culture);
        }
    }

    private void RebuildItems()
    {
        var selected = Selection.Select(r => r.Dn).ToHashSet(StringComparer.Ordinal);
        if (SelectedCheck is not { } check)
        {
            Items.ReplaceAll([]);
        }
        else
        {
            var compare = Localizer.Culture.CompareInfo;
            var rows = _rows.Values.Where(r => r.Checks.Contains(check)).ToList();
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
        }

        SelectedItem = Items.FirstOrDefault(r => selected.Contains(r.Dn));
        OnPropertyChanged(nameof(IsEmpty));
        NotifySelectionChanged();
    }

    private void RefreshCommandStates()
    {
        BatchEditCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        UnlockCommand.NotifyCanExecuteChanged();
        RequirePasswordChangeCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        ExtendCommand.NotifyCanExecuteChanged();
        ClearFlagCommand.NotifyCanExecuteChanged();
        RemoveFromGroupsCommand.NotifyCanExecuteChanged();
        HideFromGalCommand.NotifyCanExecuteChanged();
        ReadExactLastLogonCommand.NotifyCanExecuteChanged();
        SendMessageCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
    }

    private void UpdateStatus(bool stopped = false)
    {
        if (!HasRun || AsOf is not { } asOf)
        {
            StatusText = string.Empty;
            return;
        }

        var time = TimeZoneInfo.ConvertTime(asOf, Services.Time.LocalTimeZone).ToString("t", Localizer.Culture);
        var key = IsRunning ? L.AccountReview.StatusRunning : stopped ? L.AccountReview.StatusStopped : L.AccountReview.Status;
        StatusText = Localizer.Format(key, time, _readCount.ToString("N0", Localizer.Culture), _rows.Count.ToString("N0", Localizer.Culture));
    }

    /// <summary>Everything a run's evaluation needs (also used to re-evaluate rows after changes).</summary>
    private sealed class RunContext(
        IDirectorySession session,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        AccountReviewParameters parameters,
        IReadOnlySet<AccountCheck> checks,
        DomainPolicy policy,
        IReadOnlySet<string>? privileged,
        AccountReviewFormatter formatter,
        IReadOnlyList<string> attributes)
    {
        public IDirectorySession Session { get; } = session;

        public DateTimeOffset Now { get; } = now;

        public TimeZoneInfo TimeZone { get; } = timeZone;

        public AccountReviewParameters Parameters { get; } = parameters;

        public IReadOnlySet<AccountCheck> Checks { get; } = checks;

        public DomainPolicy Policy { get; } = policy;

        public IReadOnlySet<string>? Privileged { get; } = privileged;

        /// <summary>Texts of the cells (replaced when the UI language changes).</summary>
        public AccountReviewFormatter Formatter { get; set; } = formatter;

        public IReadOnlyList<string> Attributes { get; } = attributes;

        public AccountReviewFacts Evaluate(DirectoryObject obj) => AccountReviewFacts.From(
            obj,
            Now,
            TimeZone,
            Policy.MaxPasswordAge,
            lockoutTime => Policy.GetLockState(lockoutTime, Now) != LockState.NotLocked,
            Privileged);
    }

    private sealed record ScopeTarget(string BaseDn, SearchScope Scope, string? Filter);

    private sealed record ScopeRequest(AccountReviewScopeKind Kind, string? Dn, Views.SavedView? View, int InactiveDays);

    /// <summary>The scope cannot be resolved (nothing chosen, view or group gone); the message is localized.</summary>
    private sealed class ScopeException(string message) : Exception(message);
}
