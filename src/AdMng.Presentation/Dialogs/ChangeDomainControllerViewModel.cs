using AdMng.Core.Audit;
using AdMng.Core.Directory;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Dialogs;

/// <summary>
/// "Change Domain Controller" (modal; File menu, the domain root's context menu, the command palette). Lists the DCs of
/// the domain (directory + DC locator: site, client site, PDC emulator, GC, RODC; the current DC preselected), takes a
/// list entry, a typed name or "any writable DC in my site", and switches the session through
/// <see cref="ISessionManager.SwitchDomainControllerAsync"/>: a new session on the chosen DC with the current
/// connection's options and in-memory credential, verified before it replaces the current one. Failures and
/// cancellation keep the current session. The switch is recorded in the session log; the connection profile's DC is
/// changed only when the operator asks for it.
/// </summary>
public sealed partial class ChangeDomainControllerViewModel : WindowViewModelBase
{
    private readonly IAuditLog _auditLog;
    private readonly IDirectoryConnector? _connector;
    private readonly CancellationTokenSource _lifetime = new();
    private DomainControllerList _list = new([], null);
    private bool _listShown;
    private bool _syncingSelection;

    /// <param name="services">Shared dialog services.</param>
    /// <param name="auditLog">Session log (records the switch).</param>
    /// <param name="connector">DC locator (client site, DNS-registered DCs); null = directory facts only.</param>
    public ChangeDomainControllerViewModel(DialogServices services, IAuditLog auditLog, IDirectoryConnector? connector)
        : base(services)
    {
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _connector = connector;
        ProfileName = FindProfile()?.Name;
    }

    public override string WindowTitle => Localizer[L.ChangeDc.WindowTitle];

    /// <summary>"Domain: contoso.com" (empty when not connected).</summary>
    public string DomainText => Session is { } session ? Localizer.Format(L.ChangeDc.Domain, session.Info.DomainDnsName) : string.Empty;

    /// <summary>"Current domain controller: dc01.contoso.com" (empty when not connected).</summary>
    public string CurrentText => Session is { } session ? Localizer.Format(L.ChangeDc.Current, session.Info.Server) : string.Empty;

    /// <summary>The DCs of the domain (client site first, writable before read-only).</summary>
    public BulkObservableCollection<DomainControllerItem> DomainControllers { get; } = [];

    /// <summary>The list entry chosen (or matching the typed name); choosing one selects "this domain controller".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetServer), nameof(TargetHint))]
    [NotifyCanExecuteChangedFor(nameof(ChangeCommand))]
    public partial DomainControllerItem? SelectedDomainController { get; set; }

    /// <summary>Name or address of the DC (a list entry's host name, or typed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetServer), nameof(TargetHint))]
    [NotifyCanExecuteChangedFor(nameof(ChangeCommand))]
    public partial string ServerText { get; set; } = string.Empty;

    /// <summary>"Any writable domain controller in my site" instead of <see cref="ServerText"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseSpecificDomainController), nameof(TargetServer), nameof(TargetHint))]
    [NotifyCanExecuteChangedFor(nameof(ChangeCommand))]
    public partial bool UseAnyWritableInSite { get; set; }

    /// <summary>The other radio button of <see cref="UseAnyWritableInSite"/>.</summary>
    public bool UseSpecificDomainController
    {
        get => !UseAnyWritableInSite;
        set => UseAnyWritableInSite = !value;
    }

    /// <summary>The client's site is known and has a writable DC.</summary>
    public bool CanUseAnyWritableInSite => _list.AnyWritableInClientSite is not null;

    /// <summary>
    /// What "any writable DC in my site" resolves to, or why it is not available ("Loading…" until the list was read once:
    /// the DC locator may still be answering, so the client's site is not unknown yet).
    /// </summary>
    public string AnyWritableText => _list.AnyWritableInClientSite switch
    {
        null when IsLoading && !_listShown => Localizer[L.Common.Loading],
        { IsCurrent: true } current => Localizer.Format(L.ChangeDc.AnyWritableIsCurrent, current.HostName),
        { } target => Localizer.Format(L.ChangeDc.AnyWritableTarget, target.HostName, _list.ClientSite),
        null when _list.ClientSite is { } site => Localizer.Format(L.ChangeDc.NoWritableInSite, site),
        null => Localizer[L.ChangeDc.ClientSiteUnknown],
    };

    /// <summary>The host the switch goes to; null when nothing valid is chosen.</summary>
    public string? TargetServer
    {
        get
        {
            if (UseAnyWritableInSite)
            {
                return _list.AnyWritableInClientSite?.HostName;
            }

            return DomainControllerCatalog.TryNormalizeHostName(ServerText, out var host) ? host : null;
        }
    }

    /// <summary>Why the target cannot be used, or a warning about it (read-only DC); null when there is nothing to say.</summary>
    public string? TargetHint
    {
        get
        {
            if (UseAnyWritableInSite || Session is not { } session)
            {
                return null;
            }

            if (TargetServer is not { } target)
            {
                return string.IsNullOrWhiteSpace(ServerText) ? null : Localizer[L.ChangeDc.InvalidName];
            }

            if (DomainControllerCatalog.IsSameHost(target, session.Info.Server))
            {
                return Localizer[L.ChangeDc.IsCurrent];
            }

            return _list.Find(target) is { IsReadOnly: true } ? Localizer.Format(L.ChangeDc.ReadOnlyWarning, target) : null;
        }
    }

    /// <summary>Name of the saved profile the current connection came from (matching domain, authentication and security).</summary>
    public string? ProfileName { get; }

    public bool CanUpdateProfile => ProfileName is not null;

    /// <summary>"Remember the choice in the connection profile 'X'".</summary>
    public string UpdateProfileText => ProfileName is null ? string.Empty : Localizer.Format(L.ChangeDc.UpdateProfile, ProfileName);

    /// <summary>Store the chosen DC (or automatic location for "any writable DC in my site") in the profile. Off by default.</summary>
    [ObservableProperty]
    public partial bool UpdateProfile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnyWritableText))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsLoading { get; private set; }

    /// <summary>The switch is running (connecting to the chosen DC).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeCommand), nameof(RefreshCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Progress ("Connecting to dc02…") or "cancelled".</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>Why the switch failed (the session stays where it was), or "not connected".</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>Shown when the list could not be read at all (a name can still be typed).</summary>
    [ObservableProperty]
    public partial string? ListNote { get; private set; }

    /// <summary>The DC the session works with after a successful switch.</summary>
    public string? Result { get; private set; }

    /// <summary>The running (or last) list load (tests).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    public override Task InitializeAsync()
    {
        if (Session is null)
        {
            ErrorText = NoSessionMessage;
            ChangeCommand.NotifyCanExecuteChanged();
            RefreshCommand.NotifyCanExecuteChanged();
            return Task.CompletedTask;
        }

        Loading = LoadAsync();
        return Loading;
    }

    public override Task<bool> CanCloseAsync()
    {
        // Closing while connecting cancels the switch: the current session stays.
        if (IsBusy && ChangeCancelCommand.CanExecute(null))
        {
            ChangeCancelCommand.Execute(null);
        }

        return Task.FromResult(true);
    }

    [RelayCommand(CanExecute = nameof(CanChange), IncludeCancelCommand = true)]
    private async Task ChangeAsync(CancellationToken cancellationToken)
    {
        if (Session is not { } session || TargetServer is not { } target || IsBusy
            || DomainControllerCatalog.IsSameHost(target, session.Info.Server))
        {
            return;
        }

        ErrorText = null;
        StatusText = null;
        if (Services.Activities.HasRunning
            && !await Services.Dialogs.ConfirmAsync(WindowTitle, Localizer[L.ChangeDc.RunningOperations], destructive: true).ConfigureAwait(true))
        {
            return;
        }

        var previous = session.Info;
        var anyWritable = UseAnyWritableInSite;
        IsBusy = true;
        StatusText = Localizer.Format(L.Connect.Connecting, target);
        try
        {
            DomainControllerSwitch result;
            try
            {
                // Background operations of the current session are cancelled only once the new DC answered.
                result = await Services.Sessions.SwitchDomainControllerAsync(target, Services.Activities.CancelRunningAsync, cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StatusText = Localizer[L.Errors.Cancelled];
                return;
            }
            catch (SameDomainControllerException ex)
            {
                StatusText = null;
                ErrorText = Localizer.Format(L.ChangeDc.AlreadyConnected, ex.Server);
                return;
            }
            catch (Exception ex)
            {
                StatusText = null;
                var error = Describe(ex);

                // The session of the dialog may have ended meanwhile (a disconnect): then it does not stay on its DC.
                ErrorText = Session is null
                    ? NoSessionMessage
                    : error + Environment.NewLine + Localizer.Format(L.ChangeDc.StaysOn, previous.Server);
                Record(previous, previous.Server, target, error);
                return;
            }

            var server = result.Session.Info.Server;
            Result = server;
            StatusText = null;
            Record(result.Session.Info, previous.Server, server, error: null);
            await RememberInProfileAsync(anyWritable ? null : target, previous.DomainDnsName).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.ChangeDc.Changed, server));
            RequestClose(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanChange() =>
        !IsBusy && Session is { } session && TargetServer is { } target && !DomainControllerCatalog.IsSameHost(target, session.Info.Server);

    /// <summary>Cancels a running switch, otherwise closes the dialog.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy)
        {
            if (ChangeCancelCommand.CanExecute(null))
            {
                ChangeCancelCommand.Execute(null);
            }

            return;
        }

        RequestClose(false);
    }

    /// <summary>Reads the list again (directory and DC locator).</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync()
    {
        Loading = LoadAsync();
        return Loading;
    }

    private bool CanRefresh() => !IsBusy && !IsLoading && Session is not null;

    partial void OnSelectedDomainControllerChanged(DomainControllerItem? value)
    {
        if (_syncingSelection || value is null)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            UseAnyWritableInSite = false;
            ServerText = value.HostName;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    partial void OnServerTextChanged(string value)
    {
        if (_syncingSelection)
        {
            return;
        }

        // Typing selects the matching entry (or none) without overwriting what was typed.
        _syncingSelection = true;
        try
        {
            var entry = _list.Find(value);
            SelectedDomainController = entry is null ? null : DomainControllers.FirstOrDefault(i => ReferenceEquals(i.Entry, entry));
            UseAnyWritableInSite = false;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        ShowList();
        OnPropertyChanged(nameof(DomainText));
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(AnyWritableText));
        OnPropertyChanged(nameof(TargetHint));
        OnPropertyChanged(nameof(UpdateProfileText));
    }

    protected override void OnSessionEnded()
    {
        // A switch of this dialog ends its own session: it closes itself. Anything else (a disconnect) is shown.
        if (IsBusy)
        {
            return;
        }

        ErrorText = SessionEndedMessage;
        ChangeCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (ChangeCancelCommand.CanExecute(null))
            {
                ChangeCancelCommand.Execute(null);
            }

            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }

    private async Task LoadAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        var token = _lifetime.Token;
        IsLoading = true;
        ListNote = null;
        try
        {
            // Both sources are best effort and run off the UI thread; the merged list always contains the current DC.
            var directoryTask = Task.Run(() => DomainControllerCatalog.ReadAsync(session, token), token);
            var connector = _connector;
            var locatorTask = connector is null
                ? Task.FromResult<IReadOnlyList<DomainControllerInfo>>([])
                : Task.Run(() => connector.FindDomainControllersAsync(session.Info.DomainDnsName, token), token);
            var directory = await ReadOrNullAsync(directoryTask).ConfigureAwait(true);
            var located = await ReadOrNullAsync(locatorTask).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _list = DomainControllerCatalog.Merge(directory ?? [], located ?? [], session.Info);
            _listShown = true;

            // Without a locator (or when it found nothing) it returns only its fallback, the domain name: no DC is known.
            var locatedAny = located?.Any(d => !DomainControllerCatalog.IsLocatorFallback(d, session.Info.DomainDnsName)) == true;
            ListNote = directory is null && !locatedAny ? Localizer[L.ChangeDc.ListUnavailable] : null;
            if (string.IsNullOrWhiteSpace(ServerText))
            {
                ServerText = session.Info.Server;
            }

            ShowList();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static async Task<T?> ReadOrNullAsync<T>(Task<T> read)
        where T : class
    {
        try
        {
            return await read.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort (no permission on the configuration partition, no DC locator, network): the list is shorter,
            // a name can still be typed.
            return null;
        }
    }

    /// <summary>(Re)creates the rows (also after a language switch) and selects the entry of <see cref="ServerText"/>.</summary>
    private void ShowList()
    {
        _syncingSelection = true;
        try
        {
            DomainControllers.ReplaceAll(_list.Entries.Select(e => new DomainControllerItem(e, Localizer)));
            var entry = _list.Find(ServerText);
            SelectedDomainController = entry is null ? null : DomainControllers.FirstOrDefault(i => ReferenceEquals(i.Entry, entry));
        }
        finally
        {
            _syncingSelection = false;
        }

        OnPropertyChanged(nameof(CanUseAnyWritableInSite));
        OnPropertyChanged(nameof(AnyWritableText));
        OnPropertyChanged(nameof(TargetServer));
        OnPropertyChanged(nameof(TargetHint));
        ChangeCommand.NotifyCanExecuteChanged();
    }

    private string Describe(Exception exception) => exception switch
    {
        DomainControllerMismatchException mismatch => Localizer.Format(L.ChangeDc.OtherDomain, mismatch.Server, mismatch.ServerDomain, mismatch.ExpectedDomain),
        NotSupportedException => Localizer[L.ChangeDc.NotSupported],
        ArgumentException => Localizer[L.ChangeDc.InvalidName],
        ObjectDisposedException => Localizer[L.WindowSession.Ended],
        _ => Errors.TranslateConnectError(exception),
    };

    /// <summary>Session log entry of an attempt (never a password: only names and the localized error).</summary>
    private void Record(SessionInfo info, string previousServer, string requestedServer, string? error) =>
        _auditLog.Record(new AuditEntry(
            Services.Time.GetUtcNow(),
            info.BoundIdentity,
            info.Server,
            info.DefaultNamingContext,
            DomainControllerSwitch.SessionLogAction,
            Attribute: null,
            OldValue: previousServer,
            NewValue: requestedServer,
            Succeeded: error is null,
            Error: error));

    /// <summary>The saved profile the current connection came from, when it matches the session.</summary>
    private ConnectionProfile? FindProfile()
    {
        if (Session is not { } session || Services.Settings.Current.LastProfileName is not { } name)
        {
            return null;
        }

        var info = session.Info;
        var profile = Services.Settings.Current.ConnectionProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        return profile is not null
            && profile.Authentication == info.Authentication
            && profile.Security == info.Security
            && (string.IsNullOrWhiteSpace(profile.Domain)
                || DomainControllerCatalog.IsSameHost(profile.Domain, info.DomainDnsName)
                || string.Equals(profile.Domain.Trim(), info.DomainNetBiosName, StringComparison.OrdinalIgnoreCase))
            ? profile
            : null;
    }

    /// <param name="server">The chosen DC, or null for automatic location ("any writable DC in my site").</param>
    /// <param name="domainDnsName">The session's domain: automatic location needs it when the profile named only a DC.</param>
    private async Task RememberInProfileAsync(string? server, string domainDnsName)
    {
        if (!UpdateProfile || ProfileName is not { } name)
        {
            return;
        }

        try
        {
            await Services.Settings.UpdateAsync(s =>
            {
                var index = s.ConnectionProfiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.Ordinal));
                if (index >= 0)
                {
                    // Without a server and a domain the profile would connect to this computer's domain - maybe another one.
                    var profile = s.ConnectionProfiles[index];
                    s.ConnectionProfiles[index] = profile with
                    {
                        Server = server,
                        Domain = server is null && string.IsNullOrWhiteSpace(profile.Domain) ? domainDnsName : profile.Domain,
                    };
                }
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The switch is done; only the profile keeps its old DC.
            Services.Notifications.Show(NotificationSeverity.Warning, Localizer.Format(L.ChangeDc.ProfileNotSaved, Errors.Translate(ex)));
        }
    }
}
