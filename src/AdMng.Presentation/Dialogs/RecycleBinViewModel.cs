using System.Collections;
using System.Text;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Dialogs;

/// <summary>Column the Recycle Bin list is sorted by.</summary>
public enum DeletedObjectSort
{
    Name,
    Type,
    OriginalLocation,
    Deleted,
}

/// <summary>One deleted object of the Recycle Bin list.</summary>
public sealed partial class DeletedObjectRowViewModel : ObservableObject
{
    internal DeletedObjectRowViewModel(DeletedObjectInfo info, string typeText, string deletedText)
    {
        Info = info;
        TypeText = typeText;
        DeletedText = deletedText;
        Icon = ObjectIcons.For(info.Kind, disabled: false, locked: false);
    }

    public DeletedObjectInfo Info { get; }

    /// <summary>DN of the deleted object in CN=Deleted Objects.</summary>
    public string Dn => Info.DistinguishedName;

    /// <summary>The original name.</summary>
    public string Name => Info.Name;

    public ObjectKind Kind => Info.Kind;

    public ObjectIcon Icon { get; }

    /// <summary>Localized object type.</summary>
    public string TypeText { get; }

    /// <summary>Deletion time (whenChanged) in the local time zone and UI culture; empty when unknown.</summary>
    public string DeletedText { get; }

    /// <summary>The container the object was deleted from (canonical path), marked when that container is deleted too.</summary>
    [ObservableProperty]
    public partial string OriginalLocation { get; internal set; } = string.Empty;

    /// <summary>lastKnownParent as a DN (tool tip), empty when unknown.</summary>
    public string LastKnownParent => Info.LastKnownParent ?? string.Empty;
}

/// <summary>One object of the restore preview.</summary>
public sealed class RestorePreviewItemViewModel
{
    internal RestorePreviewItemViewModel(ObjectPlan plan, string typeText, string targetText, int level, RowSeverity severity, string findings)
    {
        Plan = plan;
        TypeText = typeText;
        TargetText = targetText;
        Level = level;
        Severity = severity;
        Findings = findings;
        Icon = ObjectIcons.For(plan.Kind, disabled: false, locked: false);
    }

    public ObjectPlan Plan { get; }

    public string Name => Plan.DisplayName;

    public ObjectIcon Icon { get; }

    public string TypeText { get; }

    /// <summary>The container the object is restored into (canonical path), empty when it cannot be restored.</summary>
    public string TargetText { get; }

    /// <summary>The DN the object gets back (tool tip), empty when it cannot be restored.</summary>
    public string TargetDn => Plan.Changes.FirstOrDefault(c => c.Action == ChangeAction.Restore)?.TargetDn ?? string.Empty;

    /// <summary>0 for objects restored into an existing container, n for objects below an object restored in the same plan.</summary>
    public int Level { get; }

    /// <summary>Indentation of the name (the plan's parent → child structure), in device independent pixels.</summary>
    public double Indent => Level * 18;

    public RowSeverity Severity { get; }

    /// <summary>Localized findings.</summary>
    public string Findings { get; }

    public bool CanRestore => !Plan.HasErrors && !Plan.IsNoOp;
}

/// <summary>
/// "AD Recycle Bin" window (modeless, one per application): lists the deleted objects of the domain live (CN=Deleted
/// Objects with the Show Deleted control, streamed, cancellable, server side filters), restores the selected ones to
/// their original location or to another container - a container and its children in one go, parent first - after a
/// preview of the plan, through the change pipeline (<see cref="IChangeCoordinator.ExecutePlanAsync(ChangePlan, string, CancellationToken)"/>:
/// activity, audit, PowerShell history, refresh of open lists and the tree).
/// </summary>
/// <remarks>
/// A window rather than a document tab: the shell's tabs are object lists whose commands (properties, rename, move,
/// delete...) apply to live objects; deleted objects only support restore. Without the AD Recycle Bin the window explains
/// that only tombstone reanimation is possible (most attributes are lost) and asks once more before restoring.
/// Nothing is cached: the list lives only while the window shows it and every restore is planned on fresh reads.
/// <para>The window has one status line (<see cref="Status"/>; track V, stage V2b, design note
/// docs/dev/v-interface-renewal.md "V2b-1 as built"). Two things can run at the same time - the list is read while a
/// restore is being checked - so there are two holders: the list of deleted objects says what it reads and how much,
/// that the read was stopped or why it failed (<see cref="ListStatus"/>, in front while the list is shown), and the
/// window says what it does with the selection: checking the objects to restore, restoring them
/// (<see cref="ExecutionStatus"/>) and how that ended - and, as the state that lasts, that it has no session.</para>
/// </remarks>
public sealed partial class RecycleBinViewModel : WindowViewModelBase, IHasPageStatus
{
    private const int FirstBatchSize = 50;
    private const int MaxParentReads = 200;
    private const int MaxParentDepth = 16;
    private const int MaxListedFailures = 10;

    /// <summary>How many different reasons of failed restores the status line says.</summary>
    private const int MaxSaidReasons = 3;
    private static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(150);

    private readonly SerialCancellation _loads = new();
    private readonly SerialCancellation _planning = new();
    private readonly Debouncer _reloadDebouncer;
    private readonly List<DeletedObjectRowViewModel> _rows = [];

    /// <summary>The listed deleted objects by DN key (to follow lastKnownParent chains).</summary>
    private readonly Dictionary<string, DeletedObjectInfo> _listed = new(StringComparer.Ordinal);

    /// <summary>Deleted containers that are not in the list but on the original path of listed objects (keyed by DN).</summary>
    private readonly Dictionary<string, DeletedObjectInfo> _deletedParents = new(StringComparer.Ordinal);

    /// <summary>Checks the restore that was checked last again (the Retry of a check that was stopped or failed).</summary>
    private readonly IAsyncRelayCommand _checkAgainCommand;
    private IList? _selectedItems;
    private ChangePlan? _plan;
    private string? _planTarget;
    private DeletedObjectLifetime? _lifetime;

    /// <summary>Object to select after the next load (<see cref="SelectObject"/>).</summary>
    private Guid? _pendingSelection;

    /// <summary>The session whose lifetimes were read (once per session).</summary>
    private IDirectorySession? _lifetimeSession;
    private Task _currentLoad = Task.CompletedTask;
    private int _generation;
    private bool _initialized;
    private bool _settingFilters;

    /// <summary>A name filter was typed and its debounced reload has not started yet (the list is about to change).</summary>
    private bool _filterReloadPending;

    /// <summary>What the list says at rest (<see cref="StatusText"/> without the text of a running check).</summary>
    private string _loadedText = string.Empty;

    /// <summary>Where the restore that was checked last goes: a container, or null for the original location.</summary>
    private string? _checkTarget;

    public RecycleBinViewModel(DialogServices services)
        : base(services)
    {
        Status = new PageStatusHolder(Localizer, services.Dispatcher, services.Lists.StatusLog);
        ListStatus = new PageStatusHolder(Localizer, services.Dispatcher, services.Lists.StatusLog);
        Status.Front = ListStatus;
        _checkAgainCommand = new AsyncRelayCommand(() => PreviewAsync(_checkTarget));
        _reloadDebouncer = new Debouncer(services.Time, FilterDelay);
        Kinds = CreateKinds();
        Kind = Kinds[0];
        Services.Changes.Changed += OnDirectoryChanged;
        UpdateSessionState();
    }

    public override string WindowTitle => Session is { } session
        ? Localizer.Format(L.RecycleBin.WindowTitleDomain, session.Info.DomainDnsName)
        : Localizer[L.RecycleBin.WindowTitle];

    /// <summary>
    /// The status of the window - what its one status line shows: the status of the list while the list is shown, the
    /// check and the restore of the selection, the summary of the restore preview, and that the window has no session.
    /// </summary>
    public PageStatusHolder Status { get; }

    /// <summary>
    /// The status of the list of deleted objects: its summary ("12 objects · 0.21 s · as of 14:35:02 · 3 selected"), the
    /// read with the number of objects so far, a read that was stopped, and - for as long as it lasts - why the list
    /// could not be read. The Refresh button is the Cancel of this read.
    /// </summary>
    public PageStatusHolder ListStatus { get; }

    // ---- State of the forest ----------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannerText), nameof(IsRecycleBinDisabled))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(RestoreCommand), nameof(RestoreToCommand), nameof(BrowseDeletedFromCommand),
        nameof(ConfirmRestoreCommand))]
    public partial bool IsConnected { get; private set; }

    /// <summary>The AD Recycle Bin optional feature is enabled in the forest of the session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannerText), nameof(IsRecycleBinDisabled))]
    public partial bool IsRecycleBinEnabled { get; private set; }

    /// <summary>Connected to a forest without the Recycle Bin: the banner is a warning.</summary>
    public bool IsRecycleBinDisabled => IsConnected && !IsRecycleBinEnabled;

    /// <summary>What can be restored and how (Recycle Bin enabled / tombstones only), with the lifetime when known.</summary>
    public string BannerText
    {
        get
        {
            if (!IsConnected)
            {
                return NoSessionMessage;
            }

            var text = Localizer[IsRecycleBinEnabled ? L.RecycleBin.Enabled : L.RecycleBin.Disabled];
            return _lifetime?.RestorableDays(IsRecycleBinEnabled) is { } days
                ? $"{text} {Localizer.Format(IsRecycleBinEnabled ? L.RecycleBin.Lifetime : L.RecycleBin.TombstoneLifetime, days)}"
                : text;
        }
    }

    // ---- Filters (server side) --------------------------------------------------------------------------------

    /// <summary>Contained in the original name, or the start of the name, display name or logon name.</summary>
    [ObservableProperty]
    public partial string NameFilter { get; set; } = string.Empty;

    public IReadOnlyList<Option<ObjectKind>> Kinds { get; private set; }

    /// <summary>Object type (Unknown = all).</summary>
    [ObservableProperty]
    public partial Option<ObjectKind> Kind { get; set; }

    /// <summary>Deleted on or after this local date.</summary>
    [ObservableProperty]
    public partial DateTime? DeletedAfter { get; set; }

    /// <summary>Only objects deleted from this container (lastKnownParent); null = any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeletedFromText))]
    [NotifyCanExecuteChangedFor(nameof(ClearDeletedFromCommand))]
    public partial string? DeletedFromDn { get; set; }

    public string DeletedFromText => DeletedFromDn is null ? Localizer[L.RecycleBin.AnyLocation] : AddressResolver.ToDisplayPath(DeletedFromDn);

    // ---- List -------------------------------------------------------------------------------------------------

    /// <summary>The deleted objects, sorted by <see cref="SortColumn"/>.</summary>
    public BulkObservableCollection<DeletedObjectRowViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(RestoreToCommand), nameof(CopyDnCommand))]
    public partial DeletedObjectRowViewModel? SelectedItem { get; set; }

    /// <summary>The list's multi-selection (set by the view); call <see cref="NotifySelectionChanged"/> when it changes.</summary>
    public IList? SelectedItems
    {
        get => _selectedItems;
        set
        {
            _selectedItems = value;
            NotifySelectionChanged();
        }
    }

    /// <summary>The selected rows (multi-selection, else the single selected row).</summary>
    public IReadOnlyList<DeletedObjectRowViewModel> Selection =>
        _selectedItems is { Count: > 0 } selected
            ? [.. selected.OfType<DeletedObjectRowViewModel>()]
            : SelectedItem is { } single ? [single] : [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Localized error of the last load (no access to Deleted Objects, DC unavailable...).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial string? Error { get; private set; }

    /// <summary>"12 objects · 0.21 s · as of 14:35:02".</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>"3 selected" (empty without a selection).</summary>
    [ObservableProperty]
    public partial string SelectionText { get; private set; } = string.Empty;

    /// <summary>Loaded, without error and without objects (the view shows a hint).</summary>
    public bool IsEmpty => !IsLoading && Error is null && Items.Count == 0;

    [ObservableProperty]
    public partial DeletedObjectSort SortColumn { get; private set; } = DeletedObjectSort.Deleted;

    [ObservableProperty]
    public partial bool SortDescending { get; private set; } = true;

    // ---- Restore preview --------------------------------------------------------------------------------------

    /// <summary>The restore preview replaces the list until the operator confirms or goes back.</summary>
    [ObservableProperty]
    public partial bool IsPreviewing { get; private set; }

    /// <summary>The selected objects are read again and the restore is planned (Stop or Back cancels it).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(BackCommand))]
    public partial bool IsPlanning { get; private set; }

    /// <summary>Planning or executing a restore.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(RestoreCommand), nameof(RestoreToCommand), nameof(ConfirmRestoreCommand), nameof(BackCommand))]
    public partial bool IsBusy { get; private set; }

    public BulkObservableCollection<RestorePreviewItemViewModel> PreviewItems { get; } = [];

    /// <summary>"3 of 4 objects will be restored".</summary>
    [ObservableProperty]
    public partial string PreviewSummary { get; private set; } = string.Empty;

    /// <summary>"1 object cannot be restored – see the findings", or empty.</summary>
    [ObservableProperty]
    public partial string PreviewErrorsText { get; private set; } = string.Empty;

    /// <summary>Where the objects go: the original location or the chosen container.</summary>
    [ObservableProperty]
    public partial string PreviewTargetText { get; private set; } = string.Empty;

    /// <summary>Text of the confirm button ("Restore 3 objects").</summary>
    [ObservableProperty]
    public partial string ConfirmText { get; private set; } = string.Empty;

    /// <summary>The current load (tests and the view await it).</summary>
    public Task Loading => _currentLoad;

    /// <summary>Call after the view's multi-selection changed.</summary>
    public void NotifySelectionChanged()
    {
        var count = Selection.Count;
        SelectionText = count == 0 ? string.Empty : Localizer.Format(L.RecycleBin.Selected, count);
        UpdateSummary();
        RestoreCommand.NotifyCanExecuteChanged();
        RestoreToCommand.NotifyCanExecuteChanged();
        CopyDnCommand.NotifyCanExecuteChanged();
    }

    public override async Task InitializeAsync()
    {
        _initialized = true;
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>(Re)loads the list with the current filters; a running load is cancelled. Never throws.</summary>
    public Task LoadAsync()
    {
        _reloadDebouncer.Cancel();
        _filterReloadPending = false;
        _currentLoad = LoadCoreAsync();
        return _currentLoad;
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync()
    {
        CloseResult();
        return LoadAsync();
    }

    private bool CanRefresh() => IsConnected && !IsBusy;

    /// <summary>
    /// Stops the planning of a restore (the list stays as it is), else the running load - as the operator's Stop: the
    /// status line says "Stopping…" at once and the operation ends as "stopped".
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (IsPlanning)
        {
            if (Status.Operation is { } checking)
            {
                checking.Cancel();
            }
            else
            {
                _planning.Cancel();
            }
        }
        else if (ListStatus.Operation is { } reading)
        {
            reading.Cancel();
        }
        else
        {
            _loads.Cancel();
        }
    }

    private bool CanStop() => IsLoading || IsPlanning;

    [RelayCommand]
    private void Sort(DeletedObjectSort column)
    {
        SortDescending = SortColumn == column ? !SortDescending : column == DeletedObjectSort.Deleted;
        SortColumn = column;
        ApplySort();
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task BrowseDeletedFromAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        var picked = await Services.Dialogs.PickContainerAsync(session, DeletedFromDn ?? session.Info.DefaultNamingContext).ConfigureAwait(true);
        if (picked is not null)
        {
            DeletedFromDn = picked;
        }
    }

    [RelayCommand(CanExecute = nameof(HasDeletedFrom))]
    private void ClearDeletedFrom() => DeletedFromDn = null;

    private bool HasDeletedFrom() => DeletedFromDn is not null;

    [RelayCommand]
    private Task ClearFiltersAsync()
    {
        _settingFilters = true;
        try
        {
            NameFilter = string.Empty;
            Kind = Kinds[0];
            DeletedAfter = null;
            DeletedFromDn = null;
        }
        finally
        {
            _settingFilters = false;
        }

        if (!_initialized)
        {
            return Task.CompletedTask;
        }

        CloseResult();
        return LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyDn()
    {
        var selection = Selection;
        if (selection.Count > 0)
        {
            // Not confirmed when the clipboard was busy (the clipboard service warned instead).
            ClipboardCopy.Text(Services.Clipboard, Services.Notifications, string.Join(Environment.NewLine, selection.Select(r => r.Dn)), Localizer[L.Fields.Copied]);
        }
    }

    /// <summary>Plans the restore of the selection to the original location and shows the preview.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreAsync() => PreviewAsync(null);

    /// <summary>Asks for a container, plans the restore of the selection into it and shows the preview.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreToAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        var start = Selection.Select(r => r.Info).FirstOrDefault(i => i.LastKnownParent is not null && !i.IsParentDeleted)?.LastKnownParent
            ?? session.Info.DefaultNamingContext;
        var target = await Services.Dialogs.PickContainerAsync(session, start).ConfigureAwait(true);
        if (target is not null)
        {
            await PreviewAsync(target).ConfigureAwait(true);
        }
    }

    private bool CanRestore() => IsConnected && !IsBusy && !IsPreviewing && HasSelection();

    private bool HasSelection() => Selection.Count > 0;

    /// <summary>
    /// Executes the previewed plan through the change pipeline and reloads the list. Restoring is the operation of the
    /// window (<see cref="ExecutionStatus"/>): the status line says how far it is, Stop - in the line or on the button
    /// that started it - cancels the activity, and the line says how it ended, with the domain.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmRestoreAsync()
    {
        if (_plan is not { } plan)
        {
            return;
        }

        var title = Localizer[L.RecycleBin.Restore];
        if (!IsRecycleBinEnabled
            && !await Services.Dialogs.ConfirmAsync(title, Localizer.Format(L.RecycleBin.TombstoneConfirm, ConfirmationDomain), destructive: false).ConfigureAwait(true))
        {
            return;
        }

        using var execution = ExecutionStatus.Begin(Status, Localizer, plan.Title, ConfirmationDomain, plan.Objects.Count);
        IsBusy = true;
        BatchResult? result = null;
        try
        {
            // Only in the session the restore was planned in - never with the identity of a newer session.
            result = await Services.Coordinator.ExecutePlanAsync(plan, plan.Title, RequireSession(), execution.Follow).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelled in the activity center: the activity shows it; the list shows what was restored.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            IsBusy = false;
            if (IsDisposed)
            {
                // The window was closed meanwhile: there is no line left to say it in.
                await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
            }
            else
            {
                execution.Fail(Errors.Translate(ex), ResultCode.Of(ex));
            }

            return;
        }

        IsBusy = false;
        ClosePreview();
        if (result is not null && (IsDisposed || result.FailedCount == 0))
        {
            // A restore without failures is also a toast of the main window, for which it finished elsewhere. When the
            // window was closed meanwhile the operator must still learn what was (not) restored: toast or dialog.
            await ReportAsync(title, result).ConfigureAwait(true);
        }

        if (IsDisposed)
        {
            return;
        }

        // The read after the restore belongs to it: the line keeps saying "restoring" and then says how it ended.
        await LoadAsync().ConfigureAwait(true);
        if (result is not null)
        {
            execution.Complete(result, AfterRestore(result));
        }
    }

    private bool CanConfirm() => IsConnected && IsPreviewing && !IsBusy && _plan is { ExecutableCount: > 0 };

    /// <summary>Cancels the planning, or leaves the preview and returns to the list (not while the restore runs).</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (IsPlanning)
        {
            _planning.Cancel();
        }
        else if (IsPreviewing && !IsBusy)
        {
            // What the line said was about the preview that is left (a restore that could not start).
            Status.Clear();
            ClosePreview();
        }
    }

    private bool CanGoBack() => IsPlanning || (IsPreviewing && !IsBusy);

    [RelayCommand]
    private void Close() => RequestClose(true);

    partial void OnNameFilterChanged(string value)
    {
        if (_initialized && !_settingFilters)
        {
            CloseResult();
            _filterReloadPending = true;
            _reloadDebouncer.RunAsync(_ => LoadAsync()).Forget();
        }
    }

    partial void OnKindChanged(Option<ObjectKind> value) => ReloadForFilter();

    partial void OnDeletedAfterChanged(DateTime? value) => ReloadForFilter();

    partial void OnDeletedFromDnChanged(string? value) => ReloadForFilter();

    partial void OnSelectedItemChanged(DeletedObjectRowViewModel? value) => NotifySelectionChanged();

    partial void OnIsPreviewingChanged(bool value)
    {
        // The list says its status while it is shown; the preview is the window's own (its summary).
        Status.Front = value ? null : ListStatus;
        UpdateSummary();
        RestoreCommand.NotifyCanExecuteChanged();
        RestoreToCommand.NotifyCanExecuteChanged();
        ConfirmRestoreCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        var kind = Kind.Value;
        _settingFilters = true;
        try
        {
            Kinds = CreateKinds();
            OnPropertyChanged(nameof(Kinds));
            Kind = Kinds.First(k => k.Value == kind);
        }
        finally
        {
            _settingFilters = false;
        }

        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(DeletedFromText));
        UpdateLasting();
        var infos = _rows.Select(r => r.Info).ToList();
        _rows.Clear();
        _rows.AddRange(infos.Select(CreateRow));
        UpdateLocations(_rows);
        ApplySort();
        UpdateStatus(null);
        NotifySelectionChanged();
        if (IsPreviewing && _plan is { } plan)
        {
            ShowPreview(plan, _planTarget);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Services.Changes.Changed -= OnDirectoryChanged;
            _loads.Cancel();
            _planning.Cancel();
            _reloadDebouncer.Cancel();
            _plan = null;

            // What still runs says nothing any more. A restore that is being executed goes on as an activity of the
            // connection: ending its operation does not cancel it.
            Status.Operation?.Dispose();
            Status.Front = null;
            ListStatus.Operation?.Dispose();
        }
    }

    private List<Option<ObjectKind>> CreateKinds() =>
    [
        new(ObjectKind.Unknown, Localizer[L.RecycleBin.AllKinds]),
        .. new[] { ObjectKind.User, ObjectKind.Computer, ObjectKind.Group, ObjectKind.Contact, ObjectKind.OrganizationalUnit }
            .Select(k => new Option<ObjectKind>(k, Localizer[DisplayNames.KindPluralKey(k)])),
    ];

    private void ReloadForFilter()
    {
        if (_initialized && !_settingFilters)
        {
            CloseResult();
            LoadAsync().Forget();
        }
    }

    /// <summary>
    /// The operator reads the list anew (Refresh, another filter): what the window said about its last check or restore
    /// goes - a result stays until the next operation or until it is closed (design note 6.7). A reload the window
    /// starts by itself (objects deleted elsewhere, after a restore) leaves it.
    /// </summary>
    private void CloseResult()
    {
        if (Status.Operation is null)
        {
            Status.Clear();
        }
    }

    /// <summary>
    /// The state of the window that lasts: its session ended, or it was opened without one. An error that cannot be
    /// closed - nothing can be read or restored here.
    /// </summary>
    private void UpdateLasting() => Status.SetLasting(
        SessionEndedStatus ?? (IsConnected ? null : PageStatus.Error(NoSessionMessage, new PageStatusLog(L.Errors.NotConnected), canClose: false)));

    /// <summary>
    /// The summaries of the line: of the list what was read and what is selected, of the window - only while the restore
    /// preview is shown - how many objects will be restored.
    /// </summary>
    private void UpdateSummary()
    {
        var selected = SelectionText;
        ListStatus.SetIdle(_loadedText.Length == 0 || selected.Length == 0 ? _loadedText + selected : $"{_loadedText} · {selected}");
        Status.SetIdle(IsPreviewing ? PreviewSummary : null);
    }

    private void UpdateSessionState()
    {
        var session = Session;
        IsConnected = session is not null;
        IsRecycleBinEnabled = session?.Info.RecycleBinEnabled == true;
        if (!ReferenceEquals(session, _lifetimeSession))
        {
            // Read again by the next load (the lifetimes belong to the forest of the session).
            _lifetimeSession = null;
            _lifetime = null;
        }

        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(WindowTitle));
        UpdateLasting();
    }

    /// <summary>
    /// The window does not follow a new session (possibly another identity): the list of the ended session goes,
    /// restoring is disabled and the window says it must be reopened.
    /// </summary>
    protected override void OnSessionEnded()
    {
        _loads.Cancel();
        _planning.Cancel();
        if (IsPlanning)
        {
            // The check ends without a word: the line says at once that the session ended. (A restore that is being
            // executed keeps its operation - it still says how it ended.)
            Status.Operation?.Dispose();
        }

        ClosePreview();
        UpdateSessionState();
        if (_initialized)
        {
            LoadAsync().Forget();
        }
    }

    /// <summary>Objects deleted elsewhere in the application appear without F5 (debounced, not while a restore is reviewed).</summary>
    private void OnDirectoryChanged(object? sender, DirectoryChangesEventArgs e)
    {
        if (!e.Changes.Any(c => c.Kind == DirectoryChangeKind.Deleted))
        {
            return;
        }

        _ = Services.Dispatcher.RunAsync(() =>
        {
            if (_initialized && !IsDisposed && !IsPreviewing && !IsBusy)
            {
                _reloadDebouncer.RunAsync(_ => LoadAsync()).Forget();
            }
        });
    }

    // ---- Loading ----------------------------------------------------------------------------------------------

    private async Task LoadCoreAsync()
    {
        var latest = _loads.Next();
        var generation = ++_generation;
        _rows.Clear();
        _listed.Clear();
        _deletedParents.Clear();
        Items.ReplaceAll([]);
        Error = null;
        StatusText = string.Empty;
        _loadedText = string.Empty;
        NotifySelectionChanged();
        var session = Session;
        if (session is null)
        {
            // Nothing can be read: the window says why as its state (UpdateLasting), and what the list said is over.
            ListStatus.Operation?.Dispose();
            ListStatus.SetLasting(null);
            Error = NoSessionMessage;
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        // The read is the operation of the list: the line says "working" from here on, with the number of objects so
        // far; a newer read replaces this one, and however this method is left the list does not stay in "working".
        // "The latest read wins" and the end of the session cancel it without a word; Stop - in the line or on the
        // Refresh button - is the operator's, and the read says that it was stopped.
        var domain = session.Info.DomainDnsName;
        using var reading = ListStatus.Begin(Localizer.Format(L.RecycleBinStatus.Reading, domain), new PageStatusLog(L.RecycleBinStatus.Reading));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(latest, reading.Token);
        var token = linked.Token;

        // Why the list could not be read before is over: this read decides.
        ListStatus.SetLasting(null);
        IsLoading = true;
        var started = Services.Time.GetTimestamp();
        var stopped = false;
        var code = ResultCode.None;
        try
        {
            if (!ReferenceEquals(session, _lifetimeSession))
            {
                var lifetime = await Task.Run(() => RecycleBin.ReadLifetimeAsync(session, token), token).ConfigureAwait(true);
                if (ReferenceEquals(session, Session))
                {
                    _lifetime = lifetime;
                    _lifetimeSession = session;
                    OnPropertyChanged(nameof(BannerText));
                }
            }

            var search = RecycleBin.CreateSearch(session.Info.DefaultNamingContext, CurrentFilter(), Services.Settings.Current.PageSize);
            await StreamAsync(session, search, generation, reading, domain, token).ConfigureAwait(true);
            await ResolveDeletedParentsAsync(session, generation, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            stopped = true;
        }
        catch (DirectoryException ex) when (ex.Kind is DirectoryErrorKind.NoSuchObject or DirectoryErrorKind.AccessDenied)
        {
            // AD answers noSuchObject when the operator may not list CN=Deleted Objects.
            if (generation == _generation)
            {
                Error = Localizer[L.RecycleBin.NoAccess];
                code = ResultCode.Of(ex);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation == _generation)
            {
                Error = Errors.Translate(ex);
                code = ResultCode.Of(ex);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
                UpdateLocations(_rows);
                ApplySort();
                UpdateStatus(stopped ? null : Services.Time.GetElapsedTime(started), stopped);
                OnPropertyChanged(nameof(IsEmpty));
                ApplyPendingSelection();
                if (Error is { } reason)
                {
                    // The list is empty and this is why: its state until it is read again. The read ends without a
                    // message of its own.
                    ListStatus.SetLasting(PageStatus.Error(
                        PageStatusText.Failure(Localizer.Format(L.RecycleBinStatus.ReadFailed, domain), reason, Localizer[L.Feedback.RetryNext], code),
                        new PageStatusLog(L.RecycleBinStatus.ReadFailed).WithCode(code),
                        new PageStatusAction(Localizer[L.Common.Retry], RefreshCommand)));
                }
                else if (stopped && reading.IsCancelling)
                {
                    // Only a read the operator stopped says so: what is there, and that Refresh reads the whole list.
                    reading.CompleteAttention(
                        Localizer.Format(L.RecycleBinStatus.Stopped, PageStatusText.Objects(Localizer, _rows.Count)),
                        new PageStatusLog(L.RecycleBinStatus.Stopped, _rows.Count),
                        new PageStatusAction(Localizer[L.Commands.Refresh], RefreshCommand));
                }
            }
        }
    }

    /// <summary>
    /// Selects the deleted object with <paramref name="objectGuid"/> once the list is loaded (the Watchdog tab opens a
    /// deleted object this way). The name filter is cleared so that the object is part of the list.
    /// </summary>
    public void SelectObject(Guid objectGuid)
    {
        _pendingSelection = objectGuid;
        if (!_initialized)
        {
            // The first load selects it.
            return;
        }

        if (!IsLoading && !_filterReloadPending && Items.Any(r => r.Info.ObjectGuid == objectGuid))
        {
            ApplyPendingSelection();
            return;
        }

        // Not listed, or the list is about to change (a running load or a typed filter): reload without filters (a running
        // load is cancelled); the load selects the object.
        ClearFiltersCommand.Execute(null);
    }

    private void ApplyPendingSelection()
    {
        if (_pendingSelection is { } guid && Items.FirstOrDefault(r => r.Info.ObjectGuid == guid) is { } row)
        {
            _pendingSelection = null;
            SelectedItem = row;
        }
    }

    private DeletedObjectFilter CurrentFilter()
    {
        DateTimeOffset? after = null;
        if (DeletedAfter is { } date)
        {
            var zone = Services.Time.LocalTimeZone;
            var midnight = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
            after = new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
        }

        return new DeletedObjectFilter
        {
            Name = NameFilter.Trim().Length == 0 ? null : NameFilter.Trim(),
            Kind = Kind.Value == ObjectKind.Unknown ? null : Kind.Value,
            DeletedAfter = after,
            LastKnownParent = DeletedFromDn,
        };
    }

    private Task StreamAsync(
        IDirectorySession session, DirectorySearch search, int generation, OperationScope reading, string domain, CancellationToken token)
    {
        var time = Services.Time;
        return Task.Run(
            async () =>
            {
                var buffer = new List<DeletedObjectInfo>(FirstBatchSize);
                var threshold = FirstBatchSize;
                var lastFlush = time.GetTimestamp();
                await foreach (var obj in session.SearchAsync(search, token).WithCancellation(token).ConfigureAwait(false))
                {
                    buffer.Add(DeletedObjectInfo.From(obj));
                    if (buffer.Count >= threshold || time.GetElapsedTime(lastFlush) >= FlushInterval)
                    {
                        await FlushAsync(buffer, generation, reading, domain).ConfigureAwait(false);
                        threshold = Math.Max(search.PageSize, FirstBatchSize);
                        buffer = new List<DeletedObjectInfo>(threshold);
                        lastFlush = time.GetTimestamp();
                    }
                }

                if (buffer.Count > 0)
                {
                    await FlushAsync(buffer, generation, reading, domain).ConfigureAwait(false);
                }
            },
            token);
    }

    private Task FlushAsync(List<DeletedObjectInfo> infos, int generation, OperationScope reading, string domain) => Services.Dispatcher.InvokeAsync(() =>
    {
        if (generation != _generation || IsDisposed)
        {
            return;
        }

        var rows = infos.Select(CreateRow).ToList();
        _rows.AddRange(rows);
        foreach (var row in rows)
        {
            _listed.TryAdd(Key(row.Dn), row.Info);
        }

        UpdateLocations(rows);
        Items.AddRange(rows);
        UpdateStatus(null);

        // One report per batch: the same message with the objects read so far.
        reading.Report(
            Localizer.Format(L.RecycleBinStatus.ReadingCount, domain, PageStatusText.Objects(Localizer, _rows.Count)),
            log: new PageStatusLog(L.RecycleBinStatus.Reading, _rows.Count));
    });

    /// <summary>
    /// Reads the deleted containers on the original path of listed objects that are not listed themselves (another
    /// filter, or deleted earlier), so "Original location" shows the whole path. At most <see cref="MaxParentReads"/> reads.
    /// </summary>
    private async Task ResolveDeletedParentsAsync(IDirectorySession session, int generation, CancellationToken token)
    {
        var known = new HashSet<string>(_listed.Keys, StringComparer.Ordinal);
        IEnumerable<DeletedObjectInfo> pending = _rows.Select(r => r.Info).ToList();
        var reads = 0;
        for (var depth = 0; depth < MaxParentDepth && reads < MaxParentReads; depth++)
        {
            var missing = pending
                .Where(i => i.IsParentDeleted && i.LastKnownParent is not null)
                .Select(i => i.LastKnownParent!)
                .Where(p => known.Add(Key(p)))
                .Take(MaxParentReads - reads)
                .ToList();
            if (missing.Count == 0)
            {
                return;
            }

            reads += missing.Count;
            var read = await Task.Run(() => ReadDeletedAsync(session, missing, token), token).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            foreach (var info in read)
            {
                _deletedParents[Key(info.DistinguishedName)] = info;
            }

            pending = read;
        }
    }

    private static async Task<List<DeletedObjectInfo>> ReadDeletedAsync(IDirectorySession session, IReadOnlyList<string> dns, CancellationToken token)
    {
        var result = new List<DeletedObjectInfo>();
        foreach (var dn in dns)
        {
            var search = new DirectorySearch
            {
                BaseDn = dn,
                Scope = SearchScope.Base,
                Attributes = RecycleBin.Attributes,
                SizeLimit = 1,
                IncludeDeleted = true,
            };
            try
            {
                await foreach (var obj in session.SearchAsync(search, token).ConfigureAwait(false))
                {
                    result.Add(DeletedObjectInfo.From(obj));
                }
            }
            catch (DirectoryException)
            {
                // Recycled or not visible: the location shows the container's name only.
            }
        }

        return result;
    }

    private DeletedObjectRowViewModel CreateRow(DeletedObjectInfo info)
    {
        var deleted = info.WhenChanged is { } when
            ? TimeZoneInfo.ConvertTime(when, Services.Time.LocalTimeZone).ToString("g", Localizer.Culture)
            : string.Empty;
        return new DeletedObjectRowViewModel(info, Localizer.ObjectKindName(info.Kind), deleted);
    }

    private void UpdateLocations(IEnumerable<DeletedObjectRowViewModel> rows)
    {
        foreach (var row in rows)
        {
            row.OriginalLocation = OriginalPath(row.Info, 0) is { } path
                ? path.Deleted ? Localizer.Format(L.RecycleBin.DeletedLocation, path.Path) : path.Path
                : Localizer[L.RecycleBin.UnknownLocation];
        }
    }

    /// <summary>
    /// Canonical path of the original container. When that container is deleted too, its own original path is followed
    /// through the listed or resolved deleted containers (<c>Deleted</c> = a container on the path is in the bin).
    /// </summary>
    private (string Path, bool Deleted)? OriginalPath(DeletedObjectInfo info, int depth)
    {
        if (info.LastKnownParent is not { Length: > 0 } parentDn || !DistinguishedName.TryParse(parentDn, out var parent) || parent.IsRoot)
        {
            return null;
        }

        if (!info.IsParentDeleted)
        {
            return (AddressResolver.ToDisplayPath(parentDn), false);
        }

        var key = Key(parentDn);
        if (depth >= MaxParentDepth || !(_listed.TryGetValue(key, out var parentInfo) || _deletedParents.TryGetValue(key, out parentInfo)))
        {
            return (RecycleBin.RemoveDeletedMarker(parent.Name), true);
        }

        var name = parentInfo.Name.Replace("/", "\\/", StringComparison.Ordinal);
        return OriginalPath(parentInfo, depth + 1) is { } above ? ($"{above.Path}/{name}", true) : (name, true);
    }

    private void ApplySort()
    {
        var comparer = StringComparer.Create(Localizer.Culture, ignoreCase: true);
        IEnumerable<DeletedObjectRowViewModel> sorted = SortColumn switch
        {
            DeletedObjectSort.Name => _rows.OrderBy(r => r.Name, comparer),
            DeletedObjectSort.Type => _rows.OrderBy(r => r.TypeText, comparer).ThenBy(r => r.Name, comparer),
            DeletedObjectSort.OriginalLocation => _rows.OrderBy(r => r.OriginalLocation, comparer).ThenBy(r => r.Name, comparer),
            _ => _rows.OrderBy(r => r.Info.WhenChanged ?? DateTimeOffset.MinValue).ThenBy(r => r.Name, comparer),
        };
        if (SortDescending)
        {
            sorted = sorted.Reverse();
        }

        var selected = Selection;
        Items.ReplaceAll(sorted.ToList());
        if (selected.Count == 1 && Items.Contains(selected[0]))
        {
            SelectedItem = selected[0];
        }
    }

    private void UpdateStatus(TimeSpan? elapsed, bool stopped = false)
    {
        var count = _rows.Count;
        if (Error is not null)
        {
            StatusText = string.Empty;
        }
        else if (stopped)
        {
            StatusText = Localizer.Format(L.List.Partial, count);
        }
        else if (elapsed is { } duration)
        {
            var seconds = duration.TotalSeconds.ToString("0.00", Localizer.Culture);
            StatusText = Localizer.Format(L.List.LoadSummary, count, seconds, Services.Time.GetLocalNow().ToString("T", Localizer.Culture));
        }
        else
        {
            StatusText = Localizer.Format(IsLoading ? L.RecycleBin.Loading : L.RecycleBin.Count, count);
        }

        _loadedText = StatusText;
        UpdateSummary();
    }

    private static string Key(string dn) =>
        (DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToString() : dn).ToUpperInvariant();

    // ---- Restore ----------------------------------------------------------------------------------------------

    private async Task PreviewAsync(string? targetContainerDn)
    {
        var session = Session;
        var selection = Selection;
        if (session is null || selection.Count == 0)
        {
            return;
        }

        // Checking is the operation of the window: the line says so, Stop ends it as "stopped", a failure is said
        // there, and both offer to check the same restore again. Back, closing the window and the end of its session
        // cancel it without a word. The result of a check is the preview itself: the line then shows its summary.
        _checkTarget = targetContainerDn;
        var latest = _planning.Next();
        using var checking = Status.Begin(
            Localizer.Format(L.RecycleBinStatus.Checking, selection.Count), new PageStatusLog(L.RecycleBinStatus.Checking, selection.Count));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(latest, checking.Token);
        var token = linked.Token;
        var status = StatusText;
        var planningText = Localizer[L.RecycleBin.Planning];
        IsPlanning = true;
        IsBusy = true;
        StatusText = planningText;
        try
        {
            var dns = selection.Select(r => r.Dn).ToList();
            var options = new RestorePlanningOptions
            {
                Title = Localizer.Format(L.RecycleBin.ActivityTitle, dns.Count),
                MaxDegreeOfParallelism = Services.Settings.Current.BatchMaxParallelism,
                TimeProvider = Services.Time,
            };
            var plan = await Task.Run(() => RecycleBin.PlanRestoreAsync(session, dns, targetContainerDn, options, token), token).ConfigureAwait(true);

            // Stopped in the last moment: the answer is "stopped", not a preview nobody waits for any more.
            token.ThrowIfCancellationRequested();
            if (!IsDisposed)
            {
                ShowPreview(plan, targetContainerDn);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (checking.IsCancelling)
            {
                checking.CompleteAttention(
                    PageStatusText.Failure(
                        Localizer[L.RecycleBinStatus.CheckStopped], Localizer[L.RecycleBinStatus.CheckStoppedReason], Localizer[L.Feedback.RetryNext]),
                    new PageStatusLog(L.RecycleBinStatus.CheckStopped),
                    new PageStatusAction(Localizer[L.Common.Retry], _checkAgainCommand));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var code = ResultCode.Of(ex);
            checking.CompleteError(
                PageStatusText.Failure(Localizer[L.RecycleBinStatus.CheckFailed], Errors.Translate(ex), Localizer[L.Feedback.RetryNext], code),
                new PageStatusLog(L.RecycleBinStatus.CheckFailed).WithCode(code),
                new PageStatusAction(Localizer[L.Common.Retry], _checkAgainCommand));
        }
        finally
        {
            IsPlanning = false;
            IsBusy = false;
            if (string.Equals(StatusText, planningText, StringComparison.Ordinal))
            {
                // Unless a load finished meanwhile and wrote its own status.
                StatusText = status;
            }
        }
    }

    private void ShowPreview(ChangePlan plan, string? targetContainerDn)
    {
        _plan = plan;
        _planTarget = targetContainerDn;
        var schema = Session?.Schema;
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new List<RestorePreviewItemViewModel>(plan.Objects.Count);
        foreach (var obj in plan.Objects)
        {
            var target = obj.Changes.FirstOrDefault(c => c.Action == ChangeAction.Restore)?.TargetDn;
            var level = 0;
            var targetText = string.Empty;
            if (DistinguishedName.TryParse(target, out var targetDn) && targetDn.Parent is { IsRoot: false } parent)
            {
                level = levels.TryGetValue(Key(parent.ToString()), out var parentLevel) ? parentLevel + 1 : 0;
                levels.TryAdd(Key(targetDn.ToString()), level);
                targetText = AddressResolver.ToDisplayPath(parent.ToString());
            }

            var issues = obj.Issues.Concat(obj.Changes.SelectMany(c => c.Issues)).ToList();
            var severity = issues.Count == 0 ? RowSeverity.None : issues.Max(i => i.Severity) switch
            {
                IssueSeverity.Error => RowSeverity.Error,
                IssueSeverity.Warning => RowSeverity.Warning,
                _ => RowSeverity.Info,
            };
            var findings = string.Join(" ", issues.Select(i => Errors.TranslateIssue(i, schema)).Distinct(StringComparer.Ordinal));
            items.Add(new RestorePreviewItemViewModel(obj, Localizer.ObjectKindName(obj.Kind), targetText, level, severity, findings));
        }

        PreviewItems.ReplaceAll(items);
        PreviewSummary = Localizer.Format(L.RecycleBin.PreviewSummary, plan.ExecutableCount, plan.Objects.Count);
        PreviewErrorsText = plan.ErrorCount == 0 ? string.Empty : Localizer.Format(L.RecycleBin.PreviewErrors, plan.ErrorCount);
        PreviewTargetText = targetContainerDn is null
            ? Localizer[L.RecycleBin.ToOriginalLocation]
            : Localizer.Format(L.RecycleBin.ToContainer, AddressResolver.ToDisplayPath(targetContainerDn));
        ConfirmText = Localizer.Format(L.RecycleBin.ConfirmRestore, plan.ExecutableCount);
        IsPreviewing = true;
        UpdateSummary();
        ConfirmRestoreCommand.NotifyCanExecuteChanged();
    }

    private void ClosePreview()
    {
        _plan = null;
        _planTarget = null;
        PreviewItems.ReplaceAll([]);
        IsPreviewing = false;
        ConfirmRestoreCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// What the line adds to a restore that did not bring everything back: why (the reasons of the failures, each said
    /// once) and where those objects are.
    /// </summary>
    private string? AfterRestore(BatchResult result)
    {
        if (result.FailedCount == 0 && !result.WasCancelled)
        {
            return null;
        }

        var reasons = result.Objects
            .Where(o => o.Status is ObjectResultStatus.Failed or ObjectResultStatus.PartiallySucceeded)
            .SelectMany(o => o.Changes.Where(c => !c.Succeeded).Take(1))
            .Select(c => Errors.TranslateFailure(c.ErrorKind, c.ErrorMessage))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxSaidReasons);
        return string.Join(" ", [.. reasons, Localizer[L.RecycleBinStatus.NotRestoredNext]]);
    }

    /// <summary>
    /// The outcome of a restore for the main window - a toast when nothing failed - and, when this window was closed
    /// while it restored, the failures as a dialog (an open window says them in its status line).
    /// </summary>
    private async Task ReportAsync(string title, BatchResult result)
    {
        if (result.FailedCount == 0)
        {
            Services.Notifications.Show(
                result.WasCancelled ? NotificationSeverity.Warning : NotificationSeverity.Success,
                Localizer.Format(L.RecycleBin.Restored, result.SucceededCount));
            return;
        }

        var text = new StringBuilder();
        text.AppendLine(Localizer.Format(L.Changes.Failed, result.FailedCount, result.SucceededCount));
        foreach (var failed in result.Objects
                     .Where(o => o.Status is ObjectResultStatus.Failed or ObjectResultStatus.PartiallySucceeded)
                     .Take(MaxListedFailures))
        {
            var error = failed.Changes.FirstOrDefault(c => !c.Succeeded);
            var reason = error is null ? string.Empty : Errors.TranslateFailure(error.ErrorKind, error.ErrorMessage);
            text.Append("• ").Append(failed.Plan.DisplayName).Append(": ").AppendLine(reason);
        }

        await Services.Dialogs.ShowErrorAsync(title, text.ToString().TrimEnd()).ConfigureAwait(true);
    }
}
