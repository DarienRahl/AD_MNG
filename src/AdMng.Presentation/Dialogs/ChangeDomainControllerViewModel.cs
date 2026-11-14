using AdMng.Core.Audit;
using AdMng.Core.Directory;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Feedback;
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
/// <remarks>
/// The window has one status line (<see cref="Status"/>; track V, stage V2b, design note
/// docs/dev/v-interface-renewal.md "V2b-2 as built"). The list can still be read while the operator already switches to a
/// name he typed, so there are two holders: the list of domain controllers says that it is read, how many it shows, and -
/// for as long as it lasts - that it could not be read or that its read was stopped (<see cref="ListStatus"/>, the page
/// in front); the window says the switch and why it did not happen, and - as the state that lasts - that it has no
/// session. A switch that succeeded says nothing here: the dialog closes.
/// </remarks>
public sealed partial class ChangeDomainControllerViewModel : WindowViewModelBase, IHasPageStatus
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
        Status = new PageStatusHolder(Localizer, services.Dispatcher, services.Lists.StatusLog);
        ListStatus = new PageStatusHolder(Localizer, services.Dispatcher, services.Lists.StatusLog);
        Status.Front = ListStatus;
        ProfileName = FindProfile()?.Name;
        UpdateLasting();
    }

    public override string WindowTitle => Localizer[L.ChangeDc.WindowTitle];

    /// <summary>
    /// The status of the window - what its one status line shows: what the list says while nothing else is said, the
    /// switch to the chosen domain controller (with Stop) and why it did not happen, and that the window has no session.
    /// </summary>
    public PageStatusHolder Status { get; }

    /// <summary>
    /// The status of the list of domain controllers: reading it (with Stop), how many it shows, and - for as long as it
    /// lasts - that it could not be read or that its read was stopped, with the command that reads it again.
    /// </summary>
    public PageStatusHolder ListStatus { get; }

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

    /// <summary>Progress ("Connecting to dc02…") or "cancelled" (the status line says it; kept for callers that read it).</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>
    /// Why the switch failed (the session stays where it was), or "not connected" - what the status line says, as plain
    /// text (kept for callers that read it).
    /// </summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>
    /// Set when the list could not be read at all (a name can still be typed): the state of the list in the status
    /// line, as plain text (kept for callers that read it).
    /// </summary>
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
            UpdateLasting();
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

    /// <summary>
    /// Moves the session to the chosen domain controller. The switch is the operation of the window: its status line
    /// says "Connecting to …" with Stop - the Change button is its Cancel as well, and so are the dialog's Cancel and
    /// closing it - and says why the session was not moved. A switch that succeeded says nothing: the dialog closes.
    /// </summary>
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

        // The operation of the window: "working" from here on, with Stop. The cancel of the command (the dialog's
        // Cancel, closing the window) is the same Stop; however this method is left, the line does not stay in "working".
        using var switching = Status.Begin(StatusText, new PageStatusLog(L.Connect.Connecting));
        using var stop = cancellationToken.Register(switching.Cancel);
        var token = switching.Token;
        try
        {
            DomainControllerSwitch result;
            try
            {
                // Background operations of the current session are cancelled only once the new DC answered.
                result = await Services.Sessions.SwitchDomainControllerAsync(target, Services.Activities.CancelRunningAsync, token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                StatusText = Localizer[L.Errors.Cancelled];

                // Only a switch the operator stopped says so: where the session is, and the way to try again.
                if (switching.IsCancelling)
                {
                    var again = Localizer[L.ChangeDc.ChangeButton];
                    switching.CompleteAttention(
                        Localizer.Format(L.ChangeDcStatus.Stopped, target, previous.Server, again),
                        new PageStatusLog(L.ChangeDcStatus.Stopped),
                        new PageStatusAction(again, ChangeCommand));
                }

                return;
            }
            catch (SameDomainControllerException ex)
            {
                StatusText = null;
                ErrorText = Localizer.Format(L.ChangeDc.AlreadyConnected, ex.Server);
                switching.CompleteAttention(
                    PageStatusText.Failure(Localizer[L.ChangeDcStatus.NotChanged], ErrorText), new PageStatusLog(L.ChangeDc.AlreadyConnected));
                return;
            }
            catch (Exception ex)
            {
                StatusText = null;
                var error = Describe(ex);
                if (Session is null)
                {
                    // The session of the dialog ended meanwhile (a disconnect): it does not stay on its DC. The window
                    // says so as its state; the switch ends without a message of its own.
                    ErrorText = NoSessionMessage;
                    UpdateLasting();
                }
                else
                {
                    // What was tried, why it failed, what to do and where the session is, the code. The dialog is as
                    // it was: a result that can be closed, and another domain controller can be tried right away.
                    var stays = Localizer.Format(L.ChangeDc.StaysOn, previous.Server);
                    ErrorText = error + Environment.NewLine + stays;
                    var (reason, advice, code) = Explain(ex);
                    switching.CompleteError(
                        PageStatusText.Failure(Localizer.Format(L.ChangeDcStatus.Failed, target), reason, advice is null ? stays : $"{advice} {stays}", code),
                        new PageStatusLog(L.ChangeDcStatus.Failed).WithCode(code));
                }

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

        // The window's own sentences in the new language (why the list could not be read stays as it was said).
        UpdateLasting();
        UpdateSummary();
    }

    protected override void OnSessionEnded()
    {
        // A switch of this dialog ends its own session: it closes itself. Anything else (a disconnect) is shown.
        if (IsBusy)
        {
            return;
        }

        // A read of the list that still runs ends without a word: the line says for good that the session ended.
        ListStatus.Operation?.Dispose();
        ErrorText = SessionEndedMessage;
        UpdateLasting();
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

            // What still runs says nothing any more.
            Status.Operation?.Dispose();
            Status.Front = null;
            ListStatus.Operation?.Dispose();
            _lifetime.Dispose();
        }
    }

    /// <summary>
    /// The state of the window that lasts: its session ended, or it was opened without one. An error that cannot be
    /// closed - nothing can be read or switched here. (A switch of this dialog ends its session too, but then the
    /// dialog closes: <see cref="OnSessionEnded"/> does not get here while it switches.)
    /// </summary>
    private void UpdateLasting() => Status.SetLasting(
        SessionEndedStatus ?? (Session is null ? PageStatus.Error(NoSessionMessage, new PageStatusLog(L.Errors.NotConnected), canClose: false) : null));

    /// <summary>The summary of the list: how many domain controllers it shows (nothing before it was read once).</summary>
    private void UpdateSummary() => ListStatus.SetIdle(_listShown
        ? PageStatusText.Count(Localizer, DomainControllers.Count, L.ChangeDcStatus.CountOne, L.ChangeDcStatus.CountFew, L.ChangeDcStatus.CountMany)
        : null);

    /// <summary>
    /// Reads the list - the operation of the list (<see cref="ListStatus"/>): its line says so with Stop. A read that
    /// was stopped, or that could not read anything, leaves a list that lacks domain controllers: the state of the list
    /// until it is read again, with the command that does so. Closing the window and the end of its session end the
    /// read without a word.
    /// </summary>
    private async Task LoadAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        using var reading = ListStatus.Begin(
            Localizer.Format(L.ChangeDcStatus.Reading, session.Info.DomainDnsName), new PageStatusLog(L.ChangeDcStatus.Reading));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, reading.Token);
        var token = linked.Token;
        IsLoading = true;
        ListNote = null;

        // What the last read left is over: this one decides.
        ListStatus.SetLasting(null);
        try
        {
            // Both sources are best effort and run off the UI thread; the merged list always contains the current DC.
            var directoryTask = Task.Run(() => DomainControllerCatalog.ReadAsync(session, token), token);
            var connector = _connector;
            var locatorTask = connector is null
                ? Task.FromResult<IReadOnlyList<DomainControllerInfo>>([])
                : Task.Run(() => connector.FindDomainControllersAsync(session.Info.DomainDnsName, token), token);
            var (directory, directoryError) = await ReadOrNullAsync(directoryTask).ConfigureAwait(true);
            var (located, _) = await ReadOrNullAsync(locatorTask).ConfigureAwait(true);

            // The operator's Stop: the read is asked to stop and has not ended. Every other cancellation - the window
            // closed, its session ended - leaves the list as it is and says nothing.
            var stopped = reading.IsCancelling;
            if (token.IsCancellationRequested && !stopped)
            {
                return;
            }

            _list = DomainControllerCatalog.Merge(directory ?? [], located ?? [], session.Info);
            _listShown = true;

            // Without a locator (or when it found nothing) it returns only its fallback, the domain name: no DC is known.
            var locatedAny = located?.Any(d => !DomainControllerCatalog.IsLocatorFallback(d, session.Info.DomainDnsName)) == true;
            var unavailable = !stopped && directory is null && !locatedAny;
            ListNote = unavailable ? Localizer[L.ChangeDc.ListUnavailable] : null;
            if (string.IsNullOrWhiteSpace(ServerText))
            {
                ServerText = session.Info.Server;
            }

            ShowList();
            if (stopped)
            {
                ListStatus.SetLasting(PageStatus.Attention(
                    Localizer[L.ChangeDcStatus.ReadStopped],
                    new PageStatusLog(L.ChangeDcStatus.ReadStopped),
                    new PageStatusAction(Localizer[L.Commands.Refresh], RefreshCommand)));
            }
            else if (unavailable)
            {
                // An attention, not an error: the dialog still works - a name can be typed.
                var code = ResultCode.Of(directoryError);
                ListStatus.SetLasting(PageStatus.Attention(
                    PageStatusText.Failure(
                        Localizer[L.ChangeDcStatus.ReadFailed],
                        directoryError is null ? null : Errors.Translate(directoryError),
                        Localizer[L.ChangeDcStatus.ReadFailedNext],
                        code),
                    new PageStatusLog(L.ChangeDcStatus.ReadFailed).WithCode(code),
                    new PageStatusAction(Localizer[L.Common.Retry], RefreshCommand)));
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>The result of a read, or null and why it failed.</summary>
    private static async Task<(T? Result, Exception? Error)> ReadOrNullAsync<T>(Task<T> read)
        where T : class
    {
        try
        {
            return (await read.ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            // Best effort (no permission on the configuration partition, no DC locator, network): the list is shorter,
            // a name can still be typed.
            return (null, ex);
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
        UpdateSummary();
    }

    /// <summary>Why a switch failed, as one text for the session log: the reason and, on a line of its own, the advice.</summary>
    private string Describe(Exception exception)
    {
        var (reason, advice, _) = Explain(exception);
        return advice is null ? reason : reason + Environment.NewLine + advice;
    }

    /// <summary>
    /// Why a switch failed, in the parts a status line says: the reason, what to do about it (if anything), and the
    /// result code - none for the failures this dialog explains itself.
    /// </summary>
    private (string Reason, string? Advice, ResultCode Code) Explain(Exception exception) => exception switch
    {
        DomainControllerMismatchException mismatch =>
            (Localizer.Format(L.ChangeDc.OtherDomain, mismatch.Server, mismatch.ServerDomain, mismatch.ExpectedDomain), null, ResultCode.None),
        NotSupportedException => (Localizer[L.ChangeDc.NotSupported], null, ResultCode.None),
        ArgumentException => (Localizer[L.ChangeDc.InvalidName], null, ResultCode.None),
        ObjectDisposedException => (Localizer[L.WindowSession.Ended], null, ResultCode.None),
        _ => (Errors.Translate(exception), Errors.ConnectHint(exception), ResultCode.Of(exception)),
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
        // The profile of THIS window (another window may have connected with another profile since); the global
        // "last profile" only in hosts without connection windows.
        var name = Services.Connection is { } connection ? connection.ProfileName : Services.Settings.Current.LastProfileName;
        if (Session is not { } session || name is null)
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
