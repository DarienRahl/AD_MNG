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
    private readonly Debouncer _detailsDebouncer;
    private ObjectListViewModel? _observedDocument;
    private IDirectorySession? _initializedFor;

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
        ICsvCodec csv)
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
        _searchDebouncer = new Debouncer(lists.Time, SearchDelay);
        _detailsDebouncer = new Debouncer(lists.Time, DetailsDelay);
        CommandPalette = new CommandPaletteViewModel(lists.Localizer);
        CommandPalette.Register(CreateCommandDescriptors());

        Tree.NodeActivated += OnNodeActivated;
        Sessions.SessionChanged += OnSessionChanged;
        Settings.Changed += OnSettingsChanged;
        Localizer.CultureChanged += OnCultureChanged;
        _activities.PropertyChanged += OnActivitiesChanged;
    }

    public NavigationTreeViewModel Tree { get; }

    public CommandPaletteViewModel CommandPalette { get; }

    public ActivityCenterViewModel Activities => _activities;

    /// <summary>Open documents (tabs).</summary>
    public ObservableCollection<ObjectListViewModel> Documents { get; } = [];

    [ObservableProperty]
    public partial ObjectListViewModel? SelectedDocument { get; set; }

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

    public bool IsDetailsPaneVisible => Settings.Current.ShowDetailsPane;

    public bool ShowAdvancedFeatures => Settings.Current.ShowAdvancedFeatures;

    // ---- Status bar ---------------------------------------------------------------------------------------

    public string StatusText => SelectedDocument?.StatusText ?? string.Empty;

    public string SelectionText => SelectedDocument?.SelectionText ?? string.Empty;

    /// <summary>A list is loading or a background operation runs.</summary>
    public bool IsBusy => SelectedDocument?.IsLoading == true || _activities.HasRunning;

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
    public async Task InitializeAsync()
    {
        if (Sessions.Current is { } current && ReferenceEquals(current, _initializedFor) && Documents.Count > 0)
        {
            return;
        }

        _initializedFor = Sessions.Current;
        RaiseHeaderChanged();
        foreach (var existing in Documents)
        {
            existing.Dispose();
        }

        Documents.Clear();
        SelectedDocument = null;
        SelectedObject = null;
        IsPrivilegedIdentity = false;
        var session = Sessions.Current;
        await Tree.InitializeAsync().ConfigureAwait(true);
        if (session is null)
        {
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
        var document = SelectedDocument ?? AddDocument();
        return document.NavigateAsync(location);
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
        var document = SelectedDocument ?? AddDocument();
        if (document.Location is { Kind: ListLocationKind.Container } current
            && Core.Ldap.DistinguishedName.AreEqual(current.BaseDn, location.BaseDn)
            && !document.IsLoading)
        {
            document.SelectAfterLoad(distinguishedName);
            return;
        }

        await document.NavigateAsync(location, distinguishedName).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task NewTabAsync()
    {
        var session = Sessions.Current;
        var location = SelectedDocument?.Location
            ?? (session is null ? null : ListLocation.ForContainer(session.Info.DefaultNamingContext, session.Info.DomainDnsName, ObjectIcon.Domain));
        return location is null ? Task.CompletedTask : OpenLocationInNewTabAsync(location);
    }

    [RelayCommand]
    private void CloseTab(ObjectListViewModel? document)
    {
        document ??= SelectedDocument;
        if (document is null)
        {
            return;
        }

        var index = Documents.IndexOf(document);
        Documents.Remove(document);
        document.Dispose();
        if (ReferenceEquals(SelectedDocument, document) || SelectedDocument is null)
        {
            SelectedDocument = Documents.Count == 0 ? null : Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        }
    }

    [RelayCommand]
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

    /// <summary>Double click / Enter on a row: containers open in the current tab, other objects show properties.</summary>
    [RelayCommand]
    private async Task OpenItemAsync(ObjectRowViewModel? row)
    {
        row ??= SelectedDocument?.SelectedItem;
        if (row is null)
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

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task GoBackAsync() => SelectedDocument?.GoBackCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    private bool CanGoBack() => SelectedDocument?.GoBackCommand.CanExecute(null) == true;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task GoForwardAsync() => SelectedDocument?.GoForwardCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    private bool CanGoForward() => SelectedDocument?.GoForwardCommand.CanExecute(null) == true;

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private Task GoUpAsync() => SelectedDocument?.GoUpCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    private bool CanGoUp() => SelectedDocument?.GoUpCommand.CanExecute(null) == true;

    public void Dispose()
    {
        Tree.NodeActivated -= OnNodeActivated;
        _activities.PropertyChanged -= OnActivitiesChanged;
        Sessions.SessionChanged -= OnSessionChanged;
        Settings.Changed -= OnSettingsChanged;
        Localizer.CultureChanged -= OnCultureChanged;
        _searchDebouncer.Cancel();
        _detailsDebouncer.Cancel();
        ObserveDocument(null);
        foreach (var document in Documents)
        {
            document.Dispose();
        }

        Documents.Clear();
        Tree.Dispose();
    }

    partial void OnSelectedDocumentChanged(ObjectListViewModel? value)
    {
        ObserveDocument(value);
        UpdateAddress();
        RefreshCommandStates();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(IsBusy));
        UpdateDetailsAsync().Forget();
    }

    private ObjectListViewModel AddDocument()
    {
        var document = _lists.CreateList();
        document.Navigated += OnDocumentNavigated;
        Documents.Add(document);
        SelectedDocument = document;
        return document;
    }

    private void ObserveDocument(ObjectListViewModel? document)
    {
        if (_observedDocument is not null)
        {
            _observedDocument.SelectionChanged -= OnDocumentSelectionChanged;
            _observedDocument.PropertyChanged -= OnDocumentPropertyChanged;
        }

        _observedDocument = document;
        if (document is not null)
        {
            document.SelectionChanged += OnDocumentSelectionChanged;
            document.PropertyChanged += OnDocumentPropertyChanged;
        }
    }

    private void OnDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ObjectListViewModel.StatusText):
                OnPropertyChanged(nameof(StatusText));
                break;
            case nameof(ObjectListViewModel.IsLoading):
                OnPropertyChanged(nameof(IsBusy));
                break;
            case nameof(ObjectListViewModel.Location):
                RefreshCommandStates();
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
                await Tree.ExpandToAsync(location.BaseDn).ConfigureAwait(true);
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
            foreach (var document in Documents)
            {
                document.Dispose();
            }

            Documents.Clear();
            SelectedDocument = null;
            SelectedObject = null;
            IsPrivilegedIdentity = false;
        }
    });

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsDetailsPaneVisible));
        OnPropertyChanged(nameof(ShowAdvancedFeatures));
        OnPropertyChanged(nameof(RecentContainers));
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
            catch (DirectoryException)
            {
                SelectedObject = null;
            }
        }).ConfigureAwait(true);
    }
}
