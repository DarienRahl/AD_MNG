using System.Collections.ObjectModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Views;
using AdMng.Presentation.Watchdog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// Main window: header (DC, identity, channel security), navigation tree, document tabs with object lists,
/// address bar, global quick search, details pane, command palette, status bar and all object commands.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan DetailsDelay = TimeSpan.FromMilliseconds(150);

    private readonly ObjectListServices _lists;
    private readonly ActivityCenterViewModel _activities;
    private readonly IChangeCoordinator _changes;
    private readonly ViewLibrary _views;
    private readonly IDialogService _dialogs;
    private readonly IFileDialogService _files;
    private readonly IClipboardService _clipboard;
    private readonly INotificationService _notifications;
    private readonly ICsvCodec _csv;
    private readonly IRegionalSettings _regional;
    private readonly Debouncer _detailsDebouncer;
    private readonly Func<WatchdogDocumentViewModel>? _watchdogFactory;
    private readonly WatchdogObjectHistoryLauncher? _historyLauncher;
    private ObjectListViewModel? _observedDocument;
    private IShellDocument? _observedTab;
    private IDirectorySession? _initializedFor;
    private Task? _initialization;
    private bool _detailsPaneVisible;
    private bool _disposed;

    public ShellViewModel(
        ObjectListServices lists,
        NavigationTreeViewModel tree,
        ActivityCenterViewModel activities,
        IChangeCoordinator changes,
        ViewLibrary views,
        IDialogService dialogs,
        IFileDialogService files,
        IClipboardService clipboard,
        INotificationService notifications,
        ICsvCodec csv,
        IRegionalSettings regional,
        Func<WatchdogDocumentViewModel>? watchdogFactory = null,
        WatchdogObjectHistoryLauncher? historyLauncher = null)
    {
        _lists = lists ?? throw new ArgumentNullException(nameof(lists));
        Tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
        _views = views ?? throw new ArgumentNullException(nameof(views));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _csv = csv ?? throw new ArgumentNullException(nameof(csv));
        _regional = regional ?? throw new ArgumentNullException(nameof(regional));
        _watchdogFactory = watchdogFactory;
        _historyLauncher = historyLauncher;
        _searchDebouncer = new Debouncer(lists.Time, SearchDelay);
        _detailsDebouncer = new Debouncer(lists.Time, DetailsDelay);
        CommandPalette = new CommandPaletteViewModel(lists.Localizer);
        CommandPalette.Register(CreateCommandDescriptors());

        _detailsPaneVisible = Settings.Current.ShowDetailsPane;
        Tree.NodeActivated += OnNodeActivated;
        Tree.PropertyChanged += OnTreePropertyChanged;
        Sessions.SessionChanged += OnSessionChanged;
        Settings.Changed += OnSettingsChanged;
        Localizer.CultureChanged += OnCultureChanged;
        _activities.PropertyChanged += OnActivitiesChanged;
        _views.Changed += OnViewsChanged;
        if (_historyLauncher is not null)
        {
            _historyLauncher.Requested += OnWatchdogHistoryRequested;
            _historyLauncher.AvailabilityChanged += OnWatchdogAvailabilityChanged;
        }
    }

    public NavigationTreeViewModel Tree { get; }

    public CommandPaletteViewModel CommandPalette { get; }

    public ActivityCenterViewModel Activities => _activities;

    /// <summary>Open documents (tabs): object lists and the Watchdog tab.</summary>
    public ObservableCollection<IShellDocument> Documents { get; } = [];

    /// <summary>The selected tab (bound to the tab strip).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDocument), nameof(IsDetailsPaneShown))]
    public partial IShellDocument? SelectedTab { get; set; }

    /// <summary>
    /// The selected object list, or null when no list tab is selected (the list commands disable themselves on the
    /// Watchdog tab). Setting it selects that tab.
    /// </summary>
    public ObjectListViewModel? SelectedDocument
    {
        get => SelectedTab as ObjectListViewModel;
        set => SelectedTab = value;
    }

    /// <summary>The object list tabs (without the Watchdog tab).</summary>
    public IEnumerable<ObjectListViewModel> ListDocuments => Documents.OfType<ObjectListViewModel>();

    /// <summary>A watchdog service address is configured: "Historia zmian (Watchdog)" is offered for objects.</summary>
    public bool IsWatchdogHistoryAvailable => _historyLauncher?.IsAvailable == true;

    // ---- Header -------------------------------------------------------------------------------------------

    /// <summary>"Active Directory Users and Computers – contoso.test".</summary>
    public string WindowTitle => Sessions.Current is { } s
        ? Localizer.Format(L.Shell.WindowTitleConnected, Localizer[L.App.Title], s.Info.DomainDnsName)
        : Localizer[L.App.Title];

    public bool IsConnected => Sessions.Current is not null;

    /// <summary>DC all operations go to.</summary>
    public string ServerText => Sessions.Current?.Info.Server ?? string.Empty;

    public string IdentityText => Sessions.Current?.Info.BoundIdentity ?? string.Empty;

    public string SecurityText => Sessions.Current is { } s ? Localizer[DisplayNames.SecurityKey(s.Info.Security)] : string.Empty;

    /// <summary>
    /// True when the bound identity is a member of Domain/Enterprise/Schema Admins or Administrators (best effort,
    /// via tokenGroups) - the view shows a coloured identity banner as a "you are Tier 0" reminder.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPrivilegedIdentity { get; private set; }

    // ---- Address bar --------------------------------------------------------------------------------------

    /// <summary>Editable address: canonical path (contoso.test/Firma/IT) or DN.</summary>
    [ObservableProperty]
    public partial string AddressText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? AddressError { get; private set; }

    // ---- Details pane -------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial ObjectSummaryViewModel? SelectedObject { get; private set; }

    /// <summary>The operator's choice (Ctrl+D, View → Details pane), kept in the settings.</summary>
    public bool IsDetailsPaneVisible => Settings.Current.ShowDetailsPane;

    /// <summary>
    /// The window shows the object details pane: the operator's choice while an object list (or no tab) is selected;
    /// never on a tab that shows no directory objects (the Watchdog tab has its own details pane) – the choice is
    /// kept for the lists.
    /// </summary>
    public bool IsDetailsPaneShown => IsDetailsPaneVisible && (SelectedTab is null or ObjectListViewModel);

    public bool ShowAdvancedFeatures => Settings.Current.ShowAdvancedFeatures;

    // ---- Status bar ---------------------------------------------------------------------------------------

    public string StatusText => SelectedTab?.StatusText ?? string.Empty;

    public string SelectionText => SelectedTab?.SelectionText ?? string.Empty;

    /// <summary>A tab is loading or a background operation runs.</summary>
    public bool IsBusy => SelectedTab?.IsLoading == true || _activities.HasRunning;

    /// <summary>Recent containers (MRU) for the address bar drop-down.</summary>
    public IReadOnlyList<string> RecentContainers => [.. Settings.Current.RecentContainers.Select(AddressResolver.ToDisplayPath)];

    private ISessionManager Sessions => _lists.Sessions;

    private ILocalizer Localizer => _lists.Localizer;

    private ISettingsStore Settings => _lists.Settings;

    private ErrorTranslator Errors => _lists.Errors;

    /// <summary>
    /// Builds the UI for the current session: tree, first tab (domain root), privileged identity check.
    /// Call once after the first successful connect; later reconnects (Disconnect → connect window) re-initialize
    /// automatically. Idempotent for the same session.
    /// </summary>
    public Task InitializeAsync()
    {
        // The app calls this after the first connect while SessionChanged may already have started the same
        // initialization (shell created before connecting): join it instead of building a second tree and tab.
        if (Sessions.Current is { } current && ReferenceEquals(current, _initializedFor) && _initialization is { } running
            && (!running.IsCompleted || ListDocuments.Any()))
        {
            return running;
        }

        _initialization = InitializeCoreAsync();
        return _initialization;
    }

    private async Task InitializeCoreAsync()
    {
        _initializedFor = Sessions.Current;
        RaiseHeaderChanged();
        RemoveListDocuments();
        SelectedObject = null;
        IsPrivilegedIdentity = false;
        var session = Sessions.Current;
        await Tree.InitializeAsync().ConfigureAwait(true);
        if (session is null || !ReferenceEquals(session, _initializedFor))
        {
            // Disconnected, or a newer session started its own initialization meanwhile.
            return;
        }

        CheckPrivilegedIdentityAsync(session).Forget();
        var document = AddDocument();
        await document.NavigateAsync(ListLocation.ForContainer(session.Info.DefaultNamingContext, session.Info.DomainDnsName, ObjectIcon.Domain))
            .ConfigureAwait(true);
    }

    /// <summary>Opens a location in the current tab (creates a tab when none is open).</summary>
    public Task NavigateAsync(ListLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return ListForNavigation().NavigateAsync(location);
    }

    /// <summary>Opens a location in a new tab.</summary>
    public Task OpenLocationInNewTabAsync(ListLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return AddDocument().NavigateAsync(location);
    }

    /// <summary>Opens the parent container of an object and selects the object ("locate").</summary>
    public async Task LocateAsync(string distinguishedName)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        if (!Core.Ldap.DistinguishedName.TryParse(distinguishedName, out var dn) || dn.Parent is not { IsRoot: false } parent)
        {
            return;
        }

        var location = Sessions.Current is { } s && Core.Ldap.DistinguishedName.AreEqual(parent.ToString(), s.Info.DefaultNamingContext)
            ? ListLocation.ForContainer(s.Info.DefaultNamingContext, s.Info.DomainDnsName, ObjectIcon.Domain)
            : ListLocation.ForContainer(parent.ToString());
        var document = ListForNavigation();
        if (document.Location is { Kind: ListLocationKind.Container } current
            && Core.Ldap.DistinguishedName.AreEqual(current.BaseDn, location.BaseDn)
            && !document.IsLoading)
        {
            document.SelectAfterLoad(distinguishedName);
            return;
        }

        await document.NavigateAsync(location, distinguishedName).ConfigureAwait(true);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task NewTabAsync()
    {
        var session = Sessions.Current;
        var location = SelectedDocument?.Location
            ?? (session is null ? null : ListLocation.ForContainer(session.Info.DefaultNamingContext, session.Info.DomainDnsName, ObjectIcon.Domain));
        return location is null ? Task.CompletedTask : OpenLocationInNewTabAsync(location);
    }

    /// <summary>
    /// Ctrl+W, the tab's close button: a tab with unsaved changes asks first (<see cref="IShellDocument.CanCloseAsync"/>),
    /// then the tab is removed and disposed.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CloseTabAsync(IShellDocument? document)
    {
        document ??= SelectedTab;
        if (document is null || !Documents.Contains(document))
        {
            return;
        }

        if (document.HasUnsavedChanges && (!await document.CanCloseAsync().ConfigureAwait(true) || !Documents.Contains(document)))
        {
            return;
        }

        var index = Documents.IndexOf(document);
        Documents.Remove(document);
        if (document is WatchdogDocumentViewModel watchdog)
        {
            watchdog.LocateRequested -= OnWatchdogLocateRequested;
        }

        document.Dispose();
        if (ReferenceEquals(SelectedTab, document) || SelectedTab is null)
        {
            SelectedTab = Documents.Count == 0 ? null : Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        }
    }

    /// <summary>An open tab holds unsaved changes (the main window asks before it closes).</summary>
    public bool HasUnsavedDocuments => Documents.Any(d => d.HasUnsavedChanges);

    /// <summary>
    /// The main window is closing: every tab with unsaved changes is selected and asked in turn. False when one of them
    /// keeps its changes (the window stays open); nothing is disposed here.
    /// </summary>
    public async Task<bool> CanCloseDocumentsAsync()
    {
        foreach (var document in Documents.Where(d => d.HasUnsavedChanges).ToList())
        {
            if (!Documents.Contains(document))
            {
                continue;
            }

            SelectedTab = document;
            if (!await document.CanCloseAsync().ConfigureAwait(true))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Opens the Watchdog tab (one per window) or selects it when it is open. Available without an AD session. Returns
    /// null when the application has no watchdog client.
    /// </summary>
    public WatchdogDocumentViewModel? OpenWatchdog()
    {
        if (_watchdogFactory is null || _disposed)
        {
            return null;
        }

        var watchdog = Documents.OfType<WatchdogDocumentViewModel>().FirstOrDefault();
        if (watchdog is null)
        {
            watchdog = _watchdogFactory();
            watchdog.LocateRequested += OnWatchdogLocateRequested;
            Documents.Add(watchdog);
            watchdog.InitializeAsync().Forget();
        }

        SelectedTab = watchdog;
        return watchdog;
    }

    [RelayCommand(CanExecute = nameof(CanOpenWatchdog))]
    private void ShowWatchdog() => OpenWatchdog();

    private bool CanOpenWatchdog() => _watchdogFactory is not null;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task NavigateToAddressAsync()
    {
        var session = Sessions.Current;
        AddressError = null;
        if (session is null || string.IsNullOrWhiteSpace(AddressText))
        {
            return;
        }

        try
        {
            var address = AddressText;
            var obj = await Task.Run(async () =>
            {
                var dn = await AddressResolver.ResolveAsync(session, address, CancellationToken.None).ConfigureAwait(false);
                return dn is null ? null : await session.GetAsync(dn, [Attr.ObjectClass, Attr.Name], CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(true);
            if (obj is null)
            {
                AddressError = Localizer[L.Shell.AddressNotFound];
                return;
            }

            if (obj.Kind.IsContainer())
            {
                var location = Core.Ldap.DistinguishedName.AreEqual(obj.DistinguishedName, session.Info.DefaultNamingContext)
                    ? ListLocation.ForContainer(obj.DistinguishedName, session.Info.DomainDnsName, ObjectIcon.Domain)
                    : ListLocation.ForContainer(obj.DistinguishedName, obj.Name, ObjectIcons.For(obj));
                await NavigateAsync(location).ConfigureAwait(true);
            }
            else
            {
                await LocateAsync(obj.DistinguishedName).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is DirectoryException or OperationCanceledException)
        {
            AddressError = Errors.Translate(ex);
        }
    }

    /// <summary>
    /// Double click / Enter on a row: containers open in the current tab, other objects show properties. A virtual list's
    /// "Loading…" placeholder has no object behind it and is never opened.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenItem), AllowConcurrentExecutions = true)]
    private async Task OpenItemAsync(ObjectRowViewModel? row)
    {
        row ??= SelectedDocument?.SelectedItem;
        if (row is null or { IsPlaceholder: true })
        {
            return;
        }

        if (row.IsContainer)
        {
            await NavigateAsync(ListLocation.ForContainer(row.Dn, row.Name, row.Icon)).ConfigureAwait(true);
        }
        else
        {
            await _dialogs.ShowPropertiesAsync(row.Dn).ConfigureAwait(true);
        }
    }

    private static bool CanOpenItem(ObjectRowViewModel? row) => row is not { IsPlaceholder: true };

    [RelayCommand(CanExecute = nameof(CanGoBack), AllowConcurrentExecutions = true)]
    private Task GoBackAsync() => SelectedDocument?.GoBackCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    private bool CanGoBack() => SelectedDocument?.GoBackCommand.CanExecute(null) == true;

    [RelayCommand(CanExecute = nameof(CanGoForward), AllowConcurrentExecutions = true)]
    private Task GoForwardAsync() => SelectedDocument?.GoForwardCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    private bool CanGoForward() => SelectedDocument?.GoForwardCommand.CanExecute(null) == true;

    [RelayCommand(CanExecute = nameof(CanGoUp), AllowConcurrentExecutions = true)]
    private Task GoUpAsync() => SelectedDocument?.GoUpCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    private bool CanGoUp() => SelectedDocument?.GoUpCommand.CanExecute(null) == true;

    /// <summary>
    /// Releases the subscriptions and documents. Idempotent: the window disposes the shell when it closes, and a DI
    /// container that created it disposes it again at exit (possibly on another thread - a second pass must not touch
    /// the bound collections).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Tree.NodeActivated -= OnNodeActivated;
        Tree.PropertyChanged -= OnTreePropertyChanged;
        _activities.PropertyChanged -= OnActivitiesChanged;
        _views.Changed -= OnViewsChanged;
        Sessions.SessionChanged -= OnSessionChanged;
        Settings.Changed -= OnSettingsChanged;
        Localizer.CultureChanged -= OnCultureChanged;
        if (_historyLauncher is not null)
        {
            _historyLauncher.Requested -= OnWatchdogHistoryRequested;
            _historyLauncher.AvailabilityChanged -= OnWatchdogAvailabilityChanged;
        }

        _searchDebouncer.Cancel();
        _detailsDebouncer.Cancel();
        ObserveDocument(null);
        ObserveTab(null);
        foreach (var document in Documents)
        {
            if (document is WatchdogDocumentViewModel watchdog)
            {
                watchdog.LocateRequested -= OnWatchdogLocateRequested;
            }

            document.Dispose();
        }

        Documents.Clear();
        Tree.Dispose();
    }

    partial void OnSelectedTabChanged(IShellDocument? value)
    {
        ObserveTab(value);
        ObserveDocument(value as ObjectListViewModel);
        foreach (var watchdog in Documents.OfType<WatchdogDocumentViewModel>())
        {
            // Live polling only for the visible tab; in the background the tab refreshes just its badge.
            watchdog.IsVisible = ReferenceEquals(watchdog, value);
        }

        UpdateAddress();
        RefreshCommandStates();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsVirtualListShown));
        UpdateDetailsAsync().Forget();

        // The tree highlights what the active tab shows: with another tab's container still selected, a click on that
        // node would change nothing (no navigation) and the tree's "New" would act on the wrong container.
        if ((value as ObjectListViewModel)?.Location is { } location)
        {
            SyncTreeAsync(location).Forget();
        }
    }

    private ObjectListViewModel AddDocument()
    {
        var document = _lists.CreateList();
        document.Navigated += OnDocumentNavigated;
        Documents.Add(document);
        SelectedDocument = document;
        return document;
    }

    /// <summary>
    /// The list a navigation (tree click, address, locate) goes to: the selected list, else the last list tab (selected),
    /// else a new one – navigating from the Watchdog tab never replaces it.
    /// </summary>
    private ObjectListViewModel ListForNavigation()
    {
        if (SelectedDocument is { } selected)
        {
            return selected;
        }

        if (ListDocuments.LastOrDefault() is { } existing)
        {
            SelectedDocument = existing;
            return existing;
        }

        return AddDocument();
    }

    /// <summary>Disposes and removes the object lists (a new session); the Watchdog tab stays.</summary>
    private void RemoveListDocuments()
    {
        foreach (var list in ListDocuments.ToList())
        {
            Documents.Remove(list);
            list.Dispose();
        }

        if (SelectedTab is not null && !Documents.Contains(SelectedTab))
        {
            SelectedTab = null;
        }

        SelectedTab ??= Documents.FirstOrDefault();
    }

    /// <summary>Watches the status texts of a selected tab that is not a list (lists are watched by ObserveDocument).</summary>
    private void ObserveTab(IShellDocument? tab)
    {
        if (_observedTab is not null and not ObjectListViewModel)
        {
            _observedTab.PropertyChanged -= OnTabPropertyChanged;
        }

        _observedTab = tab;
        if (tab is not null and not ObjectListViewModel)
        {
            tab.PropertyChanged += OnTabPropertyChanged;
        }
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IShellDocument.StatusText):
                OnPropertyChanged(nameof(StatusText));
                break;
            case nameof(IShellDocument.SelectionText):
                OnPropertyChanged(nameof(SelectionText));
                break;
            case nameof(IShellDocument.IsLoading):
                OnPropertyChanged(nameof(IsBusy));
                break;
        }
    }

    /// <summary>"Pokaż w AD Manager" in the Watchdog tab: the object's current DN, located in a list tab.</summary>
    private void OnWatchdogLocateRequested(object? sender, string distinguishedName) => LocateAsync(distinguishedName).Forget();

    /// <summary>"Historia zmian (Watchdog)" from an object (list, details pane, property sheet).</summary>
    private void OnWatchdogHistoryRequested(object? sender, WatchdogHistoryRequest request) => _ = _lists.Dispatcher.RunAsync(() =>
    {
        if (OpenWatchdog() is { } watchdog)
        {
            watchdog.ShowObjectHistoryAsync(request.ObjectGuid, request.Name).Forget();
        }
    });

    private void OnWatchdogAvailabilityChanged(object? sender, EventArgs e) => _ = _lists.Dispatcher.RunAsync(() =>
    {
        ShowWatchdogHistoryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsWatchdogHistoryAvailable));
    });

    private void ObserveDocument(ObjectListViewModel? document)
    {
        if (_observedDocument is not null)
        {
            _observedDocument.SelectionChanged -= OnDocumentSelectionChanged;
            _observedDocument.PropertyChanged -= OnDocumentPropertyChanged;
            _observedDocument.Items.CollectionChanged -= OnDocumentItemsChanged;
        }

        _observedDocument = document;
        if (document is not null)
        {
            document.SelectionChanged += OnDocumentSelectionChanged;
            document.PropertyChanged += OnDocumentPropertyChanged;
            document.Items.CollectionChanged += OnDocumentItemsChanged;
        }
    }

    /// <summary>Rows arrived, were filtered or removed: "Export list" depends on the shown rows.</summary>
    private void OnDocumentItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        ExportListCommand.NotifyCanExecuteChanged();

    /// <summary>Views or pins changed (also from another window): "Pin" depends on the pinned containers.</summary>
    private void OnViewsChanged(object? sender, EventArgs e) =>
        _ = _lists.Dispatcher.RunAsync(PinContainerCommand.NotifyCanExecuteChanged);

    private void OnDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ObjectListViewModel.StatusText):
                OnPropertyChanged(nameof(StatusText));
                break;
            case nameof(ObjectListViewModel.SelectionText):
                // Also raised on a language switch ("3 selected" -> "Zaznaczono: 3").
                OnPropertyChanged(nameof(SelectionText));
                break;
            case nameof(ObjectListViewModel.IsLoading):
                OnPropertyChanged(nameof(IsBusy));
                break;
            case nameof(ObjectListViewModel.Title):
                // Relocalized after a language switch, or the shown container was moved/renamed: the address follows.
                UpdateAddress();
                break;
            case nameof(ObjectListViewModel.Location):
                RefreshCommandStates();
                break;
            case nameof(ObjectListViewModel.IsVirtual):
                OnPropertyChanged(nameof(IsVirtualListShown));
                break;
        }
    }

    private void OnDocumentSelectionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectionText));
        RefreshCommandStates();
        UpdateDetailsAsync().Forget();
    }

    private void OnDocumentNavigated(object? sender, EventArgs e)
    {
        if (sender is not ObjectListViewModel document || document.Location is not { } location)
        {
            return;
        }

        if (ReferenceEquals(document, SelectedDocument))
        {
            UpdateAddress();
            RefreshCommandStates();
            SyncTreeAsync(location).Forget();
        }

        if (location.Kind == ListLocationKind.Container)
        {
            Settings.UpdateAsync(s => s.AddRecentContainer(location.BaseDn)).Forget();
            OnPropertyChanged(nameof(RecentContainers));
        }
    }

    private async Task SyncTreeAsync(ListLocation location)
    {
        try
        {
            if (location.Kind == ListLocationKind.Container)
            {
                // Expanding may read levels from the DC: select only if the active tab still shows this location
                // (fast tab switching must not end on the node of a tab that is no longer active).
                var node = await Tree.ExpandToAsync(location.BaseDn, select: false).ConfigureAwait(true);
                if (node is not null && ReferenceEquals(SelectedDocument?.Location, location))
                {
                    Tree.SelectSilently(node);
                }
            }
            else if (location.ViewId is { } id)
            {
                var node = Tree.ViewsNode.Children
                    .SelectMany(n => n is FolderNodeViewModel f ? f.Children : [n])
                    .OfType<ViewNodeViewModel>()
                    .FirstOrDefault(v => string.Equals(v.ViewId, id, StringComparison.Ordinal));
                if (node is not null)
                {
                    Tree.SelectSilently(node);
                }
            }
        }
        catch (Exception ex) when (ex is DirectoryException or OperationCanceledException)
        {
            // Tree sync is cosmetic.
        }
    }

    private void UpdateAddress()
    {
        AddressError = null;
        var location = SelectedDocument?.Location;
        AddressText = location switch
        {
            null => string.Empty,
            { Kind: ListLocationKind.Container } => AddressResolver.ToDisplayPath(location.BaseDn),
            _ => location.Title,
        };
    }

    private void OnNodeActivated(object? sender, TreeNodeViewModel node)
    {
        var session = Sessions.Current;
        if (session is null)
        {
            return;
        }

        if (node is ContainerNodeViewModel container
            && SelectedDocument?.Location is { Kind: ListLocationKind.Container } shown
            && Core.Ldap.DistinguishedName.AreEqual(shown.BaseDn, container.Dn))
        {
            // A click on the node of the container the tab already shows (F5 reloads).
            return;
        }

        ListLocation? location = node switch
        {
            ContainerNodeViewModel { Kind: ObjectKind.Domain } c => ListLocation.ForContainer(c.Dn, c.Name, ObjectIcon.Domain),
            ContainerNodeViewModel c => ListLocation.ForContainer(c.Dn, c.Name, c.IsPinnedEntry ? ObjectIcons.For(c.Kind, false, false) : c.Icon),
            ViewNodeViewModel { BuiltIn: { } builtIn } => builtIn.ToLocation(Localizer, session.Info.DefaultNamingContext, _lists.Time.GetUtcNow(), Settings.Current.InactiveDays),
            ViewNodeViewModel { SavedView: { } view } => view.ToLocation(session.Info.DefaultNamingContext),
            _ => null,
        };
        if (location is not null)
        {
            NavigateAsync(location).Forget();
        }
    }

    private void OnActivitiesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityCenterViewModel.HasRunning))
        {
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e) => _ = _lists.Dispatcher.RunAsync(() =>
    {
        RaiseHeaderChanged();
        if (Sessions.Current is { } session && !ReferenceEquals(session, _initializedFor))
        {
            InitializeAsync().Forget();
            return;
        }

        if (Sessions.Current is null)
        {
            _initializedFor = null;
            _initialization = null;
            RemoveListDocuments();
            SelectedObject = null;
            IsPrivilegedIdentity = false;

            // Drop the domain tree of the closed session (only the Views section stays).
            Tree.InitializeAsync().Forget();
        }
    });

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsDetailsPaneVisible));
        OnPropertyChanged(nameof(IsDetailsPaneShown));
        OnPropertyChanged(nameof(ShowAdvancedFeatures));
        OnPropertyChanged(nameof(RecentContainers));
        if (_detailsPaneVisible != IsDetailsPaneVisible)
        {
            // Toggled (also from the settings dialog): fill or clear the pane for the current selection.
            _detailsPaneVisible = IsDetailsPaneVisible;
            _ = _lists.Dispatcher.RunAsync(() => UpdateDetailsAsync().Forget());
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RaiseHeaderChanged();
        CommandPalette.Refresh();
        UpdateAddress();
        UpdateDetailsAsync().Forget();
    }

    private void RaiseHeaderChanged()
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ServerText));
        OnPropertyChanged(nameof(IdentityText));
        OnPropertyChanged(nameof(SecurityText));
        RefreshCommandStates();
    }

    /// <summary>Best effort: tokenGroups of the bound account contain a privileged well-known group.</summary>
    private async Task CheckPrivilegedIdentityAsync(IDirectorySession session)
    {
        var userDn = session.Info.BoundUserDn;
        if (string.IsNullOrEmpty(userDn))
        {
            return;
        }

        try
        {
            var privileged = await Task.Run(() => PrivilegeCheck.IsPrivilegedAsync(session, userDn, CancellationToken.None)).ConfigureAwait(true);
            if (ReferenceEquals(Sessions.Current, session))
            {
                IsPrivilegedIdentity = privileged;
            }
        }
        catch (Exception ex) when (ex is DirectoryException or OperationCanceledException or FormatException)
        {
            // Unknown - no banner.
        }
    }

    private async Task UpdateDetailsAsync()
    {
        var document = SelectedDocument;
        var selection = document?.Selection ?? [];
        if (!IsDetailsPaneVisible || selection.Count == 0)
        {
            _detailsDebouncer.Cancel();
            SelectedObject = null;
            return;
        }

        if (selection.Count > 1)
        {
            _detailsDebouncer.Cancel();
            SelectedObject = ObjectSummaryViewModel.ForMany([.. selection.Select(r => (r.Kind, r.IsDisabled))], Localizer);
            return;
        }

        var row = selection[0];
        await _detailsDebouncer.RunAsync(async token =>
        {
            var session = Sessions.Current;
            if (session is null)
            {
                return;
            }

            try
            {
                var obj = await Task.Run(() => session.GetAsync(row.Dn, ObjectSummaryViewModel.Attributes, token), token).ConfigureAwait(true);
                var policy = await Sessions.GetDomainPolicyAsync(token).ConfigureAwait(true);
                if (!token.IsCancellationRequested)
                {
                    SelectedObject = obj is null
                        ? null
                        : ObjectSummaryViewModel.Create(obj, Localizer, Sessions.Formatter, session.Schema, policy, _lists.Time.GetUtcNow());
                }
            }
            catch (DirectoryException) when (!token.IsCancellationRequested)
            {
                SelectedObject = null;
            }
        }).ConfigureAwait(true);
    }
}
