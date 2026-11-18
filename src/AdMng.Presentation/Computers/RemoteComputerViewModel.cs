using System.Collections.ObjectModel;
using AdMng.Core.Audit;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Computers;

/// <summary>One line of the window's action log (in memory only).</summary>
public sealed record RemoteLogEntry(DateTimeOffset At, string Text, bool IsError)
{
    public string TimeText { get; init; } = string.Empty;
}

/// <summary>A logged-on user row.</summary>
public sealed record LoggedOnUserRow(string Account, string Session, string Since, bool IsConsole);

/// <summary>A service row.</summary>
public sealed record RemoteServiceRow(string Name, string DisplayName, string State, string StartMode, string Account, bool IsAutomaticAndStopped);

/// <summary>
/// "Zdalne akcje" of one computer (K1 design note 4.5, 4.7): status (ping, WinRM, the system as the computer reports it),
/// logged-on users, services (read only), "Odśwież zasady grupy", "Uruchom ponownie…" (always confirmed), "Anuluj zaplanowane
/// ponowne uruchomienie", the local tools and the window's action log. Every call goes over WinRM with the window's
/// credential (Kerberos only), with the timeout of the settings, and stops when the window or the connection window closes.
/// Each action is audited (the restart message is not) and its PowerShell equivalent is added to the history.
/// </summary>
/// <remarks>
/// The window has one status line (<see cref="Status"/>; track V, stage V2b-4, design note docs/dev/v-interface-renewal.md
/// "V2b-4 as built"): the connection as the summary, one action at a time - a read of the status, the users or the services,
/// the refresh of Group Policy, a restart, its cancellation - as an operation with Stop, and how it ended: a read ends without
/// a message (the card or the list is the result), an action as done, a stopped one as an attention that says it is not known
/// whether the computer got the request, a failed one as an error with what to do. What lasts: the session of the window
/// ended, the computer has no DNS name, remote actions are not available. The action log of the window (a history of its own,
/// in memory) is unchanged. The six buttons that start an action are the Cancel of the one they started.
/// </remarks>
public sealed partial class RemoteComputerViewModel : WindowViewModelBase, IHasPageStatus
{
    private readonly ComputerActions _actions;
    private readonly WindowsReleaseCatalog _catalog;
    private readonly IAuditLog _auditLog;
    private readonly PowerShellHistory _powerShell;
    private readonly IRemoteComputerClient? _remote;
    private readonly IPingService? _ping;
    private readonly SerialCancellation _calls = new();
    private readonly CancellationTokenSource _closed = new();

    /// <summary>Which action runs (the flags of the six buttons follow it) and why the window cannot act at all.</summary>
    private RemoteAction _active;
    private string? _unavailable;

    public RemoteComputerViewModel(
        DialogServices services,
        ComputerActions actions,
        WindowsReleaseCatalog catalog,
        IAuditLog auditLog,
        PowerShellHistory powerShell,
        string distinguishedName,
        IRemoteComputerClient? remote = null,
        IPingService? ping = null)
        : base(services)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _powerShell = powerShell ?? throw new ArgumentNullException(nameof(powerShell));
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        DistinguishedName = distinguishedName;
        _remote = remote;
        _ping = ping;
        Name = Core.Ldap.DistinguishedName.TryParse(distinguishedName, out var dn) && !dn.IsRoot ? dn.Name : distinguishedName;
        Status = new PageStatusHolder(Localizer, services.Dispatcher, services.Lists.StatusLog);
        Status.PropertyChanged += OnStatusChanged;
        UpdateLasting();
    }

    /// <summary>What the window says in its status line (see the remarks of the class).</summary>
    public PageStatusHolder Status { get; }

    /// <summary>The window key of <see cref="DistinguishedName"/> (one window per computer per connection window).</summary>
    public static string WindowKey(string distinguishedName) => "tools:remote:" + Lists.DnKey.For(distinguishedName);

    public string DistinguishedName { get; }

    public string Name { get; private set; }

    public override string WindowTitle => Localizer.Format(L.RemoteActions.WindowTitle, Name);

    [ObservableProperty]
    public partial RemoteTarget? Target { get; private set; }

    /// <summary>"Połączenie: WinRM (HTTP 5985, Kerberos) jako CONTOSO\admin".</summary>
    [ObservableProperty]
    public partial string ConnectionText { get; private set; } = string.Empty;

    /// <summary>The status of the computer is being read - "Odśwież stan" is its Cancel.</summary>
    public bool IsReadingStatus => IsActive(RemoteAction.Status);

    /// <summary>The logged-on users are being read.</summary>
    public bool IsReadingUsers => IsActive(RemoteAction.Users);

    /// <summary>The services are being read.</summary>
    public bool IsReadingServices => IsActive(RemoteAction.Services);

    /// <summary>Group Policy is being refreshed on the computer.</summary>
    public bool IsRefreshingPolicy => IsActive(RemoteAction.Policy);

    /// <summary>A restart is being scheduled.</summary>
    public bool IsSchedulingRestart => IsActive(RemoteAction.Restart);

    /// <summary>A scheduled restart is being cancelled.</summary>
    public bool IsAbortingRestart => IsActive(RemoteAction.AbortRestart);

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string PingText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string WinRmText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SystemText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LastBootText { get; private set; } = string.Empty;

    public ObservableCollection<LoggedOnUserRow> LoggedOnUsers { get; } = [];

    /// <summary>The services (all, or only automatic and stopped ones).</summary>
    public ObservableCollection<RemoteServiceRow> ServiceRows { get; } = [];

    [ObservableProperty]
    public partial bool ShowOnlyStoppedAutomatic { get; set; }

    public ObservableCollection<RemoteLogEntry> Log { get; } = [];

    /// <summary>CIM shows no session state: disconnected RDP sessions are listed as well.</summary>
    public string UsersNote => Localizer[L.RemoteActions.UsersNote];

    public bool CanUseRemote => _remote is not null;

    private IReadOnlyList<RemoteService> _allServices = [];

    /// <summary>The action the window runs (one at a time).</summary>
    private enum RemoteAction
    {
        None,
        Status,
        Users,
        Services,
        Policy,
        Restart,
        AbortRestart,
    }

    /// <summary>How a remote action ended: what it said, how it failed, or that it was cancelled (or never started).</summary>
    private readonly record struct CallOutcome(string? Text, string? Error, RemoteErrorKind? Kind, bool Cancelled)
    {
        /// <summary>Nothing was done: there was no computer, no remote client or no session.</summary>
        public static CallOutcome None => default;

        /// <summary>The operator stopped it, or the window closed, before the computer was asked.</summary>
        public static CallOutcome Stopped => new(null, null, null, true);
    }

    public override async Task InitializeAsync()
    {
        if (Session is not { } session)
        {
            Error = NoSessionMessage;
            return;
        }

        // Why there is no computer to act on is the state of the window (its line), not a dialog on top of it.
        var (target, problem) = await _actions.FindTargetAsync(session, DistinguishedName, _closed.Token).ConfigureAwait(true);
        Target = target;
        if (Target is null)
        {
            Error = Localizer.Format(L.Computers.NoDnsName, Name);
            _unavailable = problem ?? Error;
            UpdateLasting();
            NotifyCommands();
            return;
        }

        Name = Target.Name;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(WindowTitle));
        var options = Options();
        ConnectionText = Localizer.Format(
            L.RemoteActions.ConnectionText,
            options.UseHttps ? "HTTPS" : "HTTP",
            options.EffectivePort,
            session.Info.BoundIdentity);
        if (_remote is null)
        {
            Error = Localizer[L.Computers.ToolsNotAvailable];
            _unavailable = Error;
            UpdateLasting();
        }

        NotifyCommands();
    }

    // ---- Commands ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// "Odśwież stan": ping and the operating system (with UBR and last boot). The window is busy from the first echo on
    /// (up to 8 s when the computer drops ICMP), so no other action can start meanwhile and Stop ends the ping too.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCall))]
    private Task RefreshStatusAsync() => RunExclusiveAsync(RemoteAction.Status, async token =>
    {
        if (Target is not { } target)
        {
            return CallOutcome.None;
        }

        if (_ping is not null)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _closed.Token);
            try
            {
                var result = await _ping.PingAsync(target.DnsHostName, ComputerActions.PingCount, ComputerActions.PingTimeout, linked.Token).ConfigureAwait(true);
                PingText = RemoteText.Ping(Localizer, result);
            }
            catch (RemoteActionException ex)
            {
                PingText = RemoteText.Error(Localizer, ex.Kind);
            }
            catch (OperationCanceledException)
            {
                // Stopped (or the window closed) while the echoes were on their way: nothing was read from the computer.
                return CallOutcome.Stopped;
            }
        }

        return await CallCoreAsync(token, L.RemoteActions.ActionStatus, RemoteScripts.OperatingSystem, auditAction: null, async (remote, callToken) =>
        {
            var os = await remote.GetOperatingSystemAsync(callToken).ConfigureAwait(true);
            WinRmText = Localizer[L.RemoteActions.WinRmAvailable];
            var release = os.BuildNumber is { } build && WindowsReleaseCatalog.EditionOf(os.Caption) is { } edition ? _catalog.Find(build, edition)?.Release : null;
            SystemText = release is null ? $"{os.Caption} ({os.FullBuild})" : $"{os.Caption} – {release} ({os.FullBuild})";
            LastBootText = os.LastBoot is { } boot ? Moment(boot) : string.Empty;
            return Localizer[L.RemoteActions.StatusRead];
        }).ConfigureAwait(true);
    });

    [RelayCommand(CanExecute = nameof(CanCall))]
    private Task LoadUsersAsync() => CallAsync(RemoteAction.Users, L.RemoteActions.ActionUsers, RemoteScripts.LoggedOnUsers, auditAction: null, async (remote, token) =>
    {
        var users = await remote.GetLoggedOnUsersAsync(token).ConfigureAwait(true);
        LoggedOnUsers.Clear();
        foreach (var user in users.OrderByDescending(u => u.IsConsole).ThenBy(u => u.Account, StringComparer.OrdinalIgnoreCase))
        {
            LoggedOnUsers.Add(new LoggedOnUserRow(
                user.Account,
                user.IsConsole ? Localizer[L.RemoteActions.Console] : user.SessionId?.ToString(Localizer.Culture) ?? string.Empty,
                user.Since is { } since ? Moment(since) : string.Empty,
                user.IsConsole));
        }

        return Localizer.Format(L.RemoteActions.UsersRead, users.Count);
    });

    [RelayCommand(CanExecute = nameof(CanCall))]
    private Task LoadServicesAsync() => CallAsync(RemoteAction.Services, L.RemoteActions.ActionServices, RemoteScripts.Services, auditAction: null, async (remote, token) =>
    {
        _allServices = await remote.GetServicesAsync(token).ConfigureAwait(true);
        FillServices();
        return Localizer.Format(L.RemoteActions.ServicesRead, _allServices.Count, _allServices.Count(s => s.IsAutomaticAndStopped));
    });

    /// <summary>"Odśwież zasady grupy": gpupdate /force for the computer (and the users when the setting allows it).</summary>
    [RelayCommand(CanExecute = nameof(CanCall))]
    private Task RefreshPolicyAsync() => CallAsync(RemoteAction.Policy, L.RemoteActions.ActionRefreshPolicy, RemoteScripts.RefreshPolicy, "Remote.RefreshPolicy", async (remote, token) =>
    {
        var users = Services.Settings.Current.Computers.RefreshUserPolicy;
        var result = await remote.RefreshGroupPolicyAsync(users ? GroupPolicyRefreshTarget.ComputerAndUsers : GroupPolicyRefreshTarget.Computer, token).ConfigureAwait(true);
        var text = result.Completed
            ? Localizer.Format(L.RemoteActions.PolicyRefreshed, result.FinishedAt is { } at ? Moment(at) : string.Empty)
            : Localizer[L.RemoteActions.PolicyRunning];
        return result.UserRefreshScheduled ? text + " " + Localizer[L.RemoteActions.PolicyUsersScheduled] : text;
    });

    /// <summary>"Uruchom ponownie…": the dialog (typed name for servers and DCs), then Win32ShutdownTracker.</summary>
    [RelayCommand(CanExecute = nameof(CanCall))]
    private async Task RestartAsync()
    {
        if (Target is not { } target || Services.Dialogs is not IComputerDialogs dialogs)
        {
            return;
        }

        if (await dialogs.AskRestartAsync(target).ConfigureAwait(true) is not { } request)
        {
            return;
        }

        var script = RemoteScripts.Restart(request.Delay, request.Message, request.Force);
        var newValue = string.Create(Localizer.Culture, $"delay={(int)request.Delay.TotalMinutes} min; force={request.Force}");
        await CallAsync(RemoteAction.Restart, L.RemoteActions.ActionRestart, script, "Remote.Restart", async (remote, token) =>
        {
            await remote.ScheduleRestartAsync(request.Delay, request.Message, request.Force, token).ConfigureAwait(true);
            return request.Delay == TimeSpan.Zero
                ? Localizer.Format(L.RemoteActions.RestartScheduledNow, target.Name)
                : Localizer.Format(L.RemoteActions.RestartScheduled, target.Name, (int)request.Delay.TotalMinutes);
        }, newValue).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCall))]
    private Task AbortRestartAsync() => CallAsync(RemoteAction.AbortRestart, L.RemoteActions.ActionAbortRestart, RemoteScripts.AbortRestart, "Remote.AbortRestart", async (remote, token) =>
    {
        await remote.AbortRestartAsync(token).ConfigureAwait(true);
        return Localizer[L.RemoteActions.RestartAbortRequested];
    });

    [RelayCommand(CanExecute = nameof(HasTarget))]
    private Task RemoteDesktopAsync() => LaunchAsync(LocalTool.RemoteDesktop);

    [RelayCommand(CanExecute = nameof(HasTarget))]
    private Task ComputerManagementAsync() => LaunchAsync(LocalTool.ComputerManagement);

    [RelayCommand(CanExecute = nameof(HasTarget))]
    private Task EventViewerAsync() => LaunchAsync(LocalTool.EventViewer);

    /// <summary>"Zatrzymaj": the same Stop as the one of the status line - "Stopping…" at once, then the call ends.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        Status.Operation?.Cancel();
        _calls.Cancel();
    }

    [RelayCommand]
    private Task CloseAsync() => CloseByOperatorAsync(true);

    private bool HasTarget() => Target is not null && !IsSessionEnded;

    private bool CanCall() => HasTarget() && _remote is not null && !IsBusy;

    private bool CanStop() => IsBusy;

    // ---- Lifetime ------------------------------------------------------------------------------------------------------

    protected override void OnSessionEnded()
    {
        _calls.Cancel();

        // The state first, then what still runs: the line goes from "working" straight to "the session ended".
        UpdateLasting();
        Status.Operation?.Dispose();
        NotifyCommands();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(UsersNote));
        UpdateLasting();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _calls.Cancel();
            Status.Operation?.Dispose();
            Status.PropertyChanged -= OnStatusChanged;
            _closed.Cancel();
            _closed.Dispose();
        }
    }

    partial void OnShowOnlyStoppedAutomaticChanged(bool value) => FillServices();

    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    // ---- Helpers -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One remote action at a time: a call that starts while another one runs is refused – it never cancels the running
    /// one (a restart in flight must not end as "cancelled" because a status read finished).
    /// </summary>
    private Task CallAsync(RemoteAction action, string titleKey, string command, string? auditAction, Func<IRemoteComputerSession, CancellationToken, Task<string>> call, string? auditValue = null) =>
        RunExclusiveAsync(action, token => CallCoreAsync(token, titleKey, command, auditAction, call, auditValue));

    /// <summary>
    /// Runs <paramref name="work"/> as the operation of the window, with the window busy and a token Stop cancels; refused
    /// while busy. The line says what the action is, with Stop, and how it ended.
    /// </summary>
    private async Task RunExclusiveAsync(RemoteAction action, Func<CancellationToken, Task<CallOutcome>> work)
    {
        if (IsBusy || Target is null || _remote is null || Session is null)
        {
            return;
        }

        var latest = _calls.Next();
        _active = action;
        using var operation = Status.Begin(
            Localizer.Format(L.RemoteStatus.Working, Localizer.Format(TitleKey(action), Name)),
            new PageStatusLog(TitleKey(action)),
            writes: ChangesTheComputer(action) ? OperationWrites.Other : OperationWrites.None);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(latest, operation.Token);
        IsBusy = true;
        var outcome = CallOutcome.None;
        try
        {
            outcome = await work(linked.Token).ConfigureAwait(true);
        }
        finally
        {
            if (!IsDisposed)
            {
                IsBusy = false;
            }
        }

        if (!IsDisposed && !operation.IsEnded)
        {
            Say(operation, action, outcome);
        }
    }

    /// <summary>How an action ended, in the line: a read that worked says nothing (what it read is the card or the list).</summary>
    private void Say(OperationScope operation, RemoteAction action, CallOutcome outcome)
    {
        var title = Localizer.Format(TitleKey(action), Name);
        var isRead = ReadCommand(action) is not null;
        if (outcome.Cancelled)
        {
            // Replaced, or the window closed: nothing to say. Stopped by the operator: a read is as it was, and an action
            // may or may not have reached the computer.
            if (operation.IsCancelling)
            {
                if (ReadCommand(action) is { } command)
                {
                    var again = Localizer[ReadLabelKey(action)];
                    operation.CompleteAttention(
                        Localizer.Format(L.RemoteStatus.StoppedRead, title, again),
                        new PageStatusLog(L.RemoteStatus.StoppedRead),
                        new PageStatusAction(again, command));
                }
                else
                {
                    operation.CompleteAttention(Localizer.Format(L.RemoteStatus.StoppedAction, title), new PageStatusLog(L.RemoteStatus.StoppedAction));
                }
            }

            return;
        }

        if (outcome.Error is { } error)
        {
            // An action that changes something is never repeated by a link in the line: the operator presses its button.
            var code = outcome.Kind is { } kind ? ResultCode.Remote(kind) : ResultCode.None;
            var failed = FailedKey(action);
            operation.CompleteError(
                PageStatusText.Failure(Localizer.Format(failed, Name), error, isRead ? Localizer[L.Feedback.RetryNext] : null, code),
                new PageStatusLog(failed).WithCode(code),
                isRead && ReadCommand(action) is { } retry ? new PageStatusAction(Localizer[L.Common.Retry], retry) : null);
            return;
        }

        if (outcome.Text is { } text && !isRead)
        {
            operation.CompleteDone(
                action == RemoteAction.Restart ? text : Localizer.Format(L.RemoteStatus.ActionDone, Name, text),
                new PageStatusLog(L.RemoteStatus.ActionDone));
        }
    }

    /// <summary>
    /// The actions that do something to the computer (a policy refresh, a restart, cancelling one) - the window asks before it is
    /// closed while one of them is being sent; the three reads (status, users, services) change nothing.
    /// </summary>
    private static bool ChangesTheComputer(RemoteAction action) => action is RemoteAction.Policy or RemoteAction.Restart or RemoteAction.AbortRestart;

    private static string TitleKey(RemoteAction action) => action switch
    {
        RemoteAction.Status => L.RemoteStatus.TitleStatus,
        RemoteAction.Users => L.RemoteStatus.TitleUsers,
        RemoteAction.Services => L.RemoteStatus.TitleServices,
        RemoteAction.Policy => L.RemoteStatus.TitlePolicy,
        RemoteAction.Restart => L.RemoteStatus.TitleRestart,
        _ => L.RemoteStatus.TitleAbortRestart,
    };

    private static string FailedKey(RemoteAction action) => action switch
    {
        RemoteAction.Status => L.RemoteStatus.FailedStatus,
        RemoteAction.Users => L.RemoteStatus.FailedUsers,
        RemoteAction.Services => L.RemoteStatus.FailedServices,
        RemoteAction.Policy => L.RemoteStatus.FailedPolicy,
        RemoteAction.Restart => L.RemoteStatus.FailedRestart,
        _ => L.RemoteStatus.FailedAbortRestart,
    };

    private static string ReadLabelKey(RemoteAction action) => action switch
    {
        RemoteAction.Status => L.RemoteActions.RefreshStatus,
        RemoteAction.Users => L.RemoteActions.LoadUsers,
        _ => L.RemoteActions.LoadServices,
    };

    private System.Windows.Input.ICommand? ReadCommand(RemoteAction action) => action switch
    {
        RemoteAction.Status => RefreshStatusCommand,
        RemoteAction.Users => LoadUsersCommand,
        RemoteAction.Services => LoadServicesCommand,
        _ => null,
    };

    private bool IsActive(RemoteAction action) => Status.IsWorking && _active == action;

    /// <summary>The state of the window that lasts: its session ended, it was opened without one, it has no computer to act on.</summary>
    private void UpdateLasting() => Status.SetLasting(
        SessionEndedStatus
        ?? (Session is null
            ? PageStatus.Error(NoSessionMessage, new PageStatusLog(L.Errors.NotConnected), canClose: false)
            : _unavailable is { } reason ? PageStatus.Error(reason, new PageStatusLog(L.Computers.NoDnsName), canClose: false) : null));

    partial void OnConnectionTextChanged(string value) => Status.SetIdle(value);

    /// <summary>The action that runs is the one the flags of the six buttons follow.</summary>
    private void OnStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageStatusHolder.IsWorking))
        {
            OnPropertyChanged(nameof(IsReadingStatus));
            OnPropertyChanged(nameof(IsReadingUsers));
            OnPropertyChanged(nameof(IsReadingServices));
            OnPropertyChanged(nameof(IsRefreshingPolicy));
            OnPropertyChanged(nameof(IsSchedulingRestart));
            OnPropertyChanged(nameof(IsAbortingRestart));
        }
    }

    /// <summary>
    /// One remote action: a CIM session (the window's credential, Kerberos), the call, the log line; audited when
    /// <paramref name="auditAction"/> is set; its PowerShell equivalent recorded after a success. How it ended is returned
    /// for the status line; the log and the audit say what they said before.
    /// </summary>
    private async Task<CallOutcome> CallCoreAsync(CancellationToken token, string titleKey, string command, string? auditAction, Func<IRemoteComputerSession, CancellationToken, Task<string>> call, string? auditValue = null)
    {
        if (Target is not { } target || _remote is not { } remote || Session is not { } session)
        {
            return CallOutcome.None;
        }

        var options = Options();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _closed.Token, Services.Connection?.Lifetime ?? CancellationToken.None);
        Error = null;
        var title = Localizer[titleKey];
        string? error = null;
        string? text = null;
        RemoteErrorKind? kind = null;
        var cancelled = false;
        try
        {
            await using var remoteSession = await remote.ConnectAsync(target, session, options, linked.Token).ConfigureAwait(true);
            text = await call(remoteSession, linked.Token).ConfigureAwait(true);
            AddLog($"{title}: {text}", isError: false);
            var explicitCredential = session.Info.Authentication != AuthenticationMode.CurrentUser;
            _powerShell.RecordScript(title, target.DnsHostName, RemoteScripts.Build(target.DnsHostName, options, explicitCredential, command), session.Info.DomainDnsName, Services.Time.GetUtcNow());
        }
        catch (RemoteActionException ex)
        {
            error = RemoteText.Error(Localizer, ex.Kind);
            kind = ex.Kind;
            if (ex.Kind == RemoteErrorKind.WinRmNotAvailable)
            {
                WinRmText = Localizer[L.RemoteActions.WinRmUnavailable];
            }
        }
        catch (OperationCanceledException)
        {
            error = RemoteText.Error(Localizer, RemoteErrorKind.Cancelled);
            kind = RemoteErrorKind.Cancelled;
            cancelled = true;
        }

        if (error is not null)
        {
            Error = error;
            AddLog($"{title}: {error}", isError: true);
        }

        if (auditAction is not null)
        {
            try
            {
                _auditLog.Record(new AuditEntry(
                    Services.Time.GetUtcNow(), session.Info.BoundIdentity, target.DnsHostName, target.DistinguishedName, auditAction,
                    null, null, auditValue, error is null, error));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The session log is a convenience; the action already happened.
            }
        }

        return new CallOutcome(error is null ? text : null, error, kind, cancelled);
    }

    private async Task LaunchAsync(LocalTool tool)
    {
        if (Session is { } session)
        {
            await _actions.LaunchAsync(session, tool, DistinguishedName, _closed.Token).ConfigureAwait(true);
        }
    }

    private RemoteOptions Options() => Services.Settings.Current.Computers.RemoteOptions();

    private void FillServices()
    {
        ServiceRows.Clear();
        foreach (var service in _allServices.Where(s => !ShowOnlyStoppedAutomatic || s.IsAutomaticAndStopped).OrderBy(s => s.DisplayName ?? s.Name, StringComparer.OrdinalIgnoreCase))
        {
            ServiceRows.Add(new RemoteServiceRow(
                service.Name,
                service.DisplayName ?? service.Name,
                service.State ?? string.Empty,
                service.DelayedAutoStart ? $"{service.StartMode} ({Localizer[L.RemoteActions.Delayed]})" : service.StartMode ?? string.Empty,
                service.Account ?? string.Empty,
                service.IsAutomaticAndStopped));
        }
    }

    private void AddLog(string text, bool isError)
    {
        var now = Services.Time.GetUtcNow();
        Log.Insert(0, new RemoteLogEntry(now, text, isError) { TimeText = TimeZoneInfo.ConvertTime(now, Services.Time.LocalTimeZone).ToString("T", Localizer.Culture) });
    }

    private string Moment(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Services.Time.LocalTimeZone).ToString("g", Localizer.Culture);

    private void NotifyCommands()
    {
        RefreshStatusCommand.NotifyCanExecuteChanged();
        LoadUsersCommand.NotifyCanExecuteChanged();
        LoadServicesCommand.NotifyCanExecuteChanged();
        RefreshPolicyCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        AbortRestartCommand.NotifyCanExecuteChanged();
        RemoteDesktopCommand.NotifyCanExecuteChanged();
        ComputerManagementCommand.NotifyCanExecuteChanged();
        EventViewerCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }
}
