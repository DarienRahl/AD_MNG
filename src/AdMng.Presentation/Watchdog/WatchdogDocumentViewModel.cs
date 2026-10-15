using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// The Watchdog document tab: the connection to the service (state, banner with the host it talks to, address editor –
/// https only), the caller's role (Viewer: read-only editors), the sub-tabs Events / Rules / Channels / Digest / Status
/// (the selection is remembered), live updates (<see cref="WatchdogPoller"/>: long-poll while visible, <c>/status</c>
/// every 60 s while hidden for the header badge), the rollout banner and the object history entry point. Independent of
/// the AD session (survives reconnects). Every service call is asynchronous; polling errors are shown inline.
/// </summary>
public sealed partial class WatchdogDocumentViewModel : ObservableObject, IShellDocument
{
    private readonly WatchdogServices _services;
    private readonly WatchdogPoller _poller;
    private readonly SerialCancellation _connects = new();
    private Exception? _lastError;
    private Uri? _configAddress;
    private bool _eventsLoaded;
    private bool _disposed;

    public WatchdogDocumentViewModel(WatchdogServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _poller = new WatchdogPoller(services.Client, services.Time, services.Dispatcher);
        Events = new WatchdogEventsViewModel(services, _poller);
        Config = new WatchdogConfigSession(services);
        Rules = new WatchdogRulesViewModel(services, Config);
        Channels = new WatchdogChannelsViewModel(services, Config);
        Digest = new WatchdogDigestViewModel(services, Config);
        Status = new WatchdogStatusViewModel(services);
        SelectedSubTab = Enum.TryParse<WatchdogSubTab>(services.Settings.Current.Watchdog.LastSubTab, out var tab) && Enum.IsDefined(tab) ? tab : WatchdogSubTab.Events;
        ServiceUrlInput = services.Connection.ServiceUrl ?? string.Empty;

        _poller.StatusReceived += OnStatusReceived;
        _poller.Failed += OnPollFailed;
        _poller.Recovered += OnPollRecovered;
        Events.Failed += OnCallFailed;
        Events.PropertyChanged += OnEventsPropertyChanged;
        Events.Details.LocateRequested += OnLocateRequested;
        Config.Failed += OnCallFailed;
        Status.Failed += OnCallFailed;
        Status.StatusLoaded += OnStatusLoaded;
        services.Localizer.CultureChanged += OnCultureChanged;
        services.Sessions.SessionChanged += OnSessionChanged;
        UpdateState(services.Connection.HasServiceUrl ? WatchdogConnectionState.Connecting : WatchdogConnectionState.NotConfigured, null);
    }

    /// <summary>Open the object with this DN in an object list of the main window ("Otwórz w AD_MNG").</summary>
    public event EventHandler<string>? LocateRequested;

    // ---- IShellDocument ---------------------------------------------------------------------------------------

    public string Title => _services.Localizer[L.Watchdog.Title];

    public ObjectIcon Icon => ObjectIcon.Watchdog;

    public bool IsLoading => ConnectionState == WatchdogConnectionState.Connecting || Events.IsLoading;

    public string StatusText => ConnectionState == WatchdogConnectionState.Connected ? Events.StatusText : ConnectionMessage;

    public string SelectionText => SelectedSubTab == WatchdogSubTab.Events ? Events.SelectionText : string.Empty;

    // ---- Connection -------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsNotConfigured), nameof(ShowAddressEditor), nameof(HasConnectionProblem), nameof(IsLoading), nameof(StatusText), nameof(ShowRolloutBanner))]
    public partial WatchdogConnectionState ConnectionState { get; private set; }

    /// <summary>Localized explanation of the state (with the remedy for Kerberos / role problems).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string ConnectionMessage { get; private set; } = string.Empty;

    /// <summary>Short state name for the banner ("Połączono", "Brak uprawnień").</summary>
    [ObservableProperty]
    public partial string StateText { get; private set; } = string.Empty;

    /// <summary>"admng-wd.contoso.com:8443" – always visible in the banner (credentials go only to this host).</summary>
    [ObservableProperty]
    public partial string HostText { get; private set; } = string.Empty;

    public bool IsConnected => ConnectionState == WatchdogConnectionState.Connected;

    public bool IsNotConfigured => ConnectionState == WatchdogConnectionState.NotConfigured;

    /// <summary>The connection failed (banner shows the error with Retry and Change address).</summary>
    public bool HasConnectionProblem => ConnectionState is not (WatchdogConnectionState.Connected or WatchdogConnectionState.Connecting or WatchdogConnectionState.NotConfigured);

    /// <summary>The operator chose "Zmień adres".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAddressEditor))]
    public partial bool IsEditingAddress { get; private set; }

    public bool ShowAddressEditor => IsNotConfigured || IsEditingAddress;

    /// <summary>"Adres usługi Watchdog", e.g. https://admng-wd.contoso.com:8443.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string ServiceUrlInput { get; set; }

    [ObservableProperty]
    public partial string? ServiceUrlError { get; private set; }

    /// <summary>A live update failed (shown inline; the poller retries).</summary>
    [ObservableProperty]
    public partial string? LiveError { get; private set; }

    // ---- Caller -----------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdmin), nameof(RoleText), nameof(AccountText))]
    public partial MeDto? Me { get; private set; }

    public bool IsAdmin => Me?.Roles.Contains(WatchdogRole.Admin) == true;

    /// <summary>"Administrator" / "Przeglądający".</summary>
    public string RoleText => Me is null ? string.Empty : _services.Localizer[WatchdogDisplay.RoleKey(IsAdmin ? WatchdogRole.Admin : WatchdogRole.Viewer)];

    /// <summary>"CONTOSO\jan.kowalski · Administrator · szczegółowość: pełna".</summary>
    public string AccountText => Me is null
        ? string.Empty
        : $"{Me.Account} · {RoleText} · {_services.Localizer.Format(L.Watchdog.DetailLevelLabel, _services.Localizer[WatchdogDisplay.DetailLevelKey(Me.DetailLevel)])}";

    // ---- Sub-tabs ---------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSubTabIndex), nameof(SelectionText))]
    public partial WatchdogSubTab SelectedSubTab { get; set; }

    /// <summary><see cref="SelectedSubTab"/> as an index (the view's tab strip).</summary>
    public int SelectedSubTabIndex
    {
        get => (int)SelectedSubTab;
        set => SelectedSubTab = Enum.IsDefined((WatchdogSubTab)value) ? (WatchdogSubTab)value : WatchdogSubTab.Events;
    }

    public WatchdogEventsViewModel Events { get; }

    public WatchdogConfigSession Config { get; }

    public WatchdogRulesViewModel Rules { get; }

    public WatchdogChannelsViewModel Channels { get; }

    public WatchdogDigestViewModel Digest { get; }

    public WatchdogStatusViewModel Status { get; }

    /// <summary>The tab is the selected document (set by the view): live polling while visible, badge only while hidden.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    // ---- Badge and rollout ------------------------------------------------------------------------------------

    /// <summary>Unacknowledged High/Critical events of the last 24 h (tab header badge).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge), nameof(BadgeText))]
    public partial int UnacknowledgedHighOrAbove { get; private set; }

    public bool HasBadge => UnacknowledgedHighOrAbove > 0;

    public string BadgeText => UnacknowledgedHighOrAbove > 99 ? "99+" : UnacknowledgedHighOrAbove.ToString(_services.Localizer.Culture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRolloutActive), nameof(RolloutBannerText), nameof(ShowRolloutBanner))]
    public partial DateTimeOffset? RolloutUntil { get; private set; }

    public bool IsRolloutActive => RolloutUntil is not null;

    /// <summary>The rollout banner is shown (connected and rollout mode active).</summary>
    public bool ShowRolloutBanner => IsConnected && IsRolloutActive;

    public string RolloutBannerText => Rules.RolloutBannerText;

    /// <summary>The current connection attempt (tests await it).</summary>
    public Task Connecting { get; private set; } = Task.CompletedTask;

    /// <summary>Connects when a service address is configured, else shows the address editor. Never throws.</summary>
    public Task InitializeAsync()
    {
        Connecting = ConnectCoreAsync();
        return Connecting;
    }

    /// <summary>F5: reconnects after a failure, else reloads the current sub-tab.</summary>
    public Task RefreshAsync()
    {
        if (!IsConnected)
        {
            return InitializeAsync();
        }

        return SelectedSubTab switch
        {
            WatchdogSubTab.Events => Events.LoadAsync(),
            WatchdogSubTab.Status => Status.LoadAsync(),
            _ => Config.IsDirty ? Task.CompletedTask : Config.LoadAsync(),
        };
    }

    /// <summary>"Historia zmian (Watchdog)": the events of one object.</summary>
    public Task ShowObjectHistoryAsync(Guid objectGuid, string? name)
    {
        SelectedSubTab = WatchdogSubTab.Events;
        if (!IsConnected)
        {
            Events.Filter.ShowObject(objectGuid, name);
            return ConnectionState == WatchdogConnectionState.Connecting ? Connecting : InitializeAsync();
        }

        return Events.ShowObjectAsync(objectGuid, name);
    }

    /// <summary>"Historia zmian (Watchdog)" without a name.</summary>
    public void ShowObjectHistory(Guid objectGuid) => ShowObjectHistoryAsync(objectGuid, null).Forget();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connects.Cancel();
        _services.Localizer.CultureChanged -= OnCultureChanged;
        _services.Sessions.SessionChanged -= OnSessionChanged;
        _poller.StatusReceived -= OnStatusReceived;
        _poller.Failed -= OnPollFailed;
        _poller.Recovered -= OnPollRecovered;
        _poller.Dispose();
        Events.Failed -= OnCallFailed;
        Events.PropertyChanged -= OnEventsPropertyChanged;
        Events.Details.LocateRequested -= OnLocateRequested;
        Config.Failed -= OnCallFailed;
        Status.Failed -= OnCallFailed;
        Status.StatusLoaded -= OnStatusLoaded;
        Events.Dispose();
        Rules.Dispose();
        Channels.Dispose();
        Digest.Dispose();
    }

    /// <summary>Saves the typed address (https only) and connects.</summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (ShowAddressEditor || !_services.Connection.HasServiceUrl)
        {
            var error = await _services.Connection.SetAddressAsync(ServiceUrlInput).ConfigureAwait(true);
            ServiceUrlError = error;
            if (error is not null)
            {
                return;
            }

            IsEditingAddress = false;
        }

        await InitializeAsync().ConfigureAwait(true);
    }

    private bool CanConnect() => !string.IsNullOrWhiteSpace(ServiceUrlInput) || _services.Connection.HasServiceUrl;

    /// <summary>"Zmień adres": shows the address editor with the current address.</summary>
    [RelayCommand]
    private void ChangeAddress()
    {
        ServiceUrlInput = _services.Connection.ServiceUrl ?? string.Empty;
        ServiceUrlError = null;
        IsEditingAddress = true;
    }

    [RelayCommand]
    private void CancelChangeAddress()
    {
        IsEditingAddress = false;
        ServiceUrlError = null;
    }

    [RelayCommand]
    private Task RefreshNowAsync() => RefreshAsync();

    partial void OnIsVisibleChanged(bool value)
    {
        _poller.IsVisible = value;
        if (value && IsConnected && !_eventsLoaded)
        {
            _eventsLoaded = true;
            Events.LoadAsync().Forget();
        }
    }

    partial void OnSelectedSubTabChanged(WatchdogSubTab value)
    {
        var name = value.ToString();
        if (!string.Equals(_services.Settings.Current.Watchdog.LastSubTab, name, StringComparison.Ordinal))
        {
            _services.Settings.UpdateAsync(s => s.Watchdog.LastSubTab = name).Forget();
        }

        if (value == WatchdogSubTab.Status && IsConnected)
        {
            Status.LoadAsync().Forget();
        }
    }

    private async Task ConnectCoreAsync()
    {
        var token = _connects.Next();
        LiveError = null;
        if (!_services.Connection.HasServiceUrl)
        {
            _poller.Stop();
            UpdateState(WatchdogConnectionState.NotConfigured, null);
            return;
        }

        try
        {
            _services.Connection.EnsureConfigured();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UpdateState(WatchdogConnectionState.Error, ex);
            return;
        }

        UpdateState(WatchdogConnectionState.Connecting, null);
        try
        {
            var client = _services.Client;
            var me = await client.GetMeAsync(token).ConfigureAwait(true);
            var catalog = await client.GetCatalogAsync(token).ConfigureAwait(true);
            var status = await client.GetStatusAsync(token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            SetCaller(me);
            Events.Filter.SetCatalog(catalog);
            ApplyStatus(status);
            UpdateState(WatchdogConnectionState.Connected, null);
            _poller.IsVisible = IsVisible;
            _poller.Start();
            _eventsLoaded = IsVisible;
            var loads = new List<Task>();

            // Retry / F5 after a connection problem reconnects to the same service: the Admin's unsaved edits (and typed
            // secrets) stay; a different service or a lost Admin role loads its own configuration.
            var address = client.BaseAddress;
            if (!(Config.IsDirty && IsAdmin && _configAddress is not null && _configAddress == address))
            {
                _configAddress = address;
                loads.Add(Config.LoadAsync());
            }

            if (IsVisible)
            {
                loads.Add(Events.LoadAsync());
            }

            if (SelectedSubTab == WatchdogSubTab.Status)
            {
                loads.Add(Status.LoadAsync());
            }

            await Task.WhenAll(loads).ConfigureAwait(true);
            Rules.LoadRolloutCountsAsync().Forget();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && token.IsCancellationRequested)
        {
            // A newer attempt or the tab was closed (the client reports the cancelled request as an
            // OperationCanceledException or a WatchdogClientException of kind Cancelled): no error state.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UpdateState(WatchdogConnectionService.StateFor(ex), ex);
        }
    }

    private void SetCaller(MeDto me)
    {
        Me = me;
        Config.IsAdmin = IsAdmin;
        Status.IsAdmin = IsAdmin;
        Events.Account = me.Account;
        Events.Details.Account = me.Account;
        Rules.Account = me.Account;
    }

    private void ApplyStatus(StatusDto status)
    {
        UnacknowledgedHighOrAbove = status.Last24Hours.UnacknowledgedHighOrAbove;
        Rules.ApplyStatus(status);
        RolloutUntil = status.RolloutModeUntil;
        OnPropertyChanged(nameof(RolloutBannerText));
        Status.ApplyStatus(status);
    }

    private void UpdateState(WatchdogConnectionState state, Exception? error)
    {
        var localizer = _services.Localizer;
        _lastError = error ?? (state is WatchdogConnectionState.Connected or WatchdogConnectionState.Connecting or WatchdogConnectionState.NotConfigured ? null : _lastError);
        error = _lastError;
        ConnectionState = state;
        HostText = _services.Connection.HostText;
        StateText = localizer[WatchdogDisplay.StateKey(state)];
        ConnectionMessage = state switch
        {
            WatchdogConnectionState.NotConfigured => localizer[L.Watchdog.ConnectionNotConfigured],
            WatchdogConnectionState.Connecting => localizer.Format(L.Watchdog.ConnectionConnecting, HostText),
            WatchdogConnectionState.Connected => localizer.Format(L.Watchdog.ConnectionConnected, HostText),
            _ when error is not null => _services.Connection.Describe(error),
            _ => localizer[WatchdogDisplay.StateKey(state)],
        };
        if (state == WatchdogConnectionState.NotConfigured)
        {
            ServiceUrlInput = _services.Connection.ServiceUrl ?? ServiceUrlInput;
        }

        OnPropertyChanged(nameof(IsLoading));
    }

    private void OnStatusReceived(object? sender, StatusDto status)
    {
        ApplyStatus(status);
        RestoreConnection();
    }

    private void OnStatusLoaded(object? sender, StatusDto status)
    {
        UnacknowledgedHighOrAbove = status.Last24Hours.UnacknowledgedHighOrAbove;
        RolloutUntil = status.RolloutModeUntil;
        Rules.ApplyStatus(status);
        OnPropertyChanged(nameof(RolloutBannerText));
        RestoreConnection();
    }

    /// <summary>A call reached the service after a connection problem: the tab is usable again without Retry.</summary>
    private void RestoreConnection()
    {
        if (HasConnectionProblem && Me is not null)
        {
            UpdateState(WatchdogConnectionState.Connected, null);
        }
    }

    private void OnPollFailed(object? sender, Exception error)
    {
        LiveError = _services.Localizer.Format(L.Watchdog.LiveError, _services.Connection.Describe(error));
        if (error is WatchdogClientException { Kind: WatchdogClientErrorKind.Unauthorized or WatchdogClientErrorKind.Forbidden or WatchdogClientErrorKind.ApiVersionMismatch })
        {
            UpdateState(WatchdogConnectionService.StateFor(error), error);
        }
    }

    private void OnPollRecovered(object? sender, EventArgs e)
    {
        LiveError = null;
        RestoreConnection();
    }

    /// <summary>
    /// A single request of a sub-tab failed (its error is shown inline there). Authentication, role, certificate and API
    /// version problems concern the connection and change the banner. A timeout or a network failure of one request
    /// does not hide the tab while the poller still reaches the service – only when live updates fail too
    /// (the poller is backing off), and the next successful poll or status restores the tab.
    /// </summary>
    private void OnCallFailed(object? sender, Exception error)
    {
        if (!WatchdogConnectionService.IsConnectionFailure(error) || error is not WatchdogClientException { Kind: not WatchdogClientErrorKind.NotConfigured } failure)
        {
            return;
        }

        if ((failure.Kind is WatchdogClientErrorKind.Timeout or WatchdogClientErrorKind.Unreachable) && _poller.CurrentBackoff == TimeSpan.Zero)
        {
            return;
        }

        UpdateState(WatchdogConnectionService.StateFor(error), error);
    }

    private void OnEventsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WatchdogEventsViewModel.IsLoading):
                OnPropertyChanged(nameof(IsLoading));
                break;
            case nameof(WatchdogEventsViewModel.StatusText):
                OnPropertyChanged(nameof(StatusText));
                break;
            case nameof(WatchdogEventsViewModel.SelectionText):
                OnPropertyChanged(nameof(SelectionText));
                break;
        }
    }

    private void OnLocateRequested(object? sender, string dn) => LocateRequested?.Invoke(this, dn);

    /// <summary>An AD session was opened or closed: the pickers and "Otwórz w AD_MNG" re-evaluate their availability.</summary>
    private void OnSessionChanged(object? sender, EventArgs e) => _services.Dispatcher.RunAsync(() =>
    {
        if (_disposed)
        {
            return;
        }

        Events.NotifySessionChanged();
        Rules.NotifySessionChanged();
    }).Forget();

    private void OnCultureChanged(object? sender, EventArgs e) => _services.Dispatcher.RunAsync(() =>
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(AccountText));
        UpdateState(ConnectionState, null);
        Events.Relocalize();
        Config.Relocalize();
        Rules.Relocalize();
        Channels.Relocalize();
        Digest.Relocalize();
        Status.Relocalize();
        OnPropertyChanged(nameof(RolloutBannerText));
    }).Forget();
}
