using System.Collections.ObjectModel;
using AdMng.Core.Audit;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
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
public sealed partial class RemoteComputerViewModel : WindowViewModelBase
{
    private readonly ComputerActions _actions;
    private readonly WindowsReleaseCatalog _catalog;
    private readonly IAuditLog _auditLog;
    private readonly PowerShellHistory _powerShell;
    private readonly IRemoteComputerClient? _remote;
    private readonly IPingService? _ping;
    private readonly SerialCancellation _calls = new();
    private readonly CancellationTokenSource _closed = new();

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
    }

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

    public override async Task InitializeAsync()
    {
        if (Session is not { } session)
        {
            Error = NoSessionMessage;
            return;
        }

        Target = await _actions.ResolveTargetAsync(session, DistinguishedName, Localizer[L.RemoteActions.WindowTitleShort], _closed.Token).ConfigureAwait(true);
        if (Target is null)
        {
            Error = Localizer.Format(L.Computers.NoDnsName, Name);
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
        }

        NotifyCommands();
    }

    // ---- Commands ----------------------------------------------------------------------------------------------------

    /// <summary>"Odśwież stan": ping and the operating system (with UBR and last boot).</summary>
    [RelayCommand(CanExecute = nameof(CanCall))]
    private async Task RefreshStatusAsync()
    {
        if (Target is not { } target)
        {
            return;
        }

        if (_ping is not null)
        {
            try
            {
                var result = await _ping.PingAsync(target.DnsHostName, ComputerActions.PingCount, ComputerActions.PingTimeout, _closed.Token).ConfigureAwait(true);
                PingText = RemoteText.Ping(Localizer, result);
            }
            catch (RemoteActionException ex)
            {
                PingText = RemoteText.Error(Localizer, ex.Kind);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        await CallAsync(L.RemoteActions.ActionStatus, RemoteScripts.OperatingSystem, auditAction: null, async (remote, token) =>
        {
            var os = await remote.GetOperatingSystemAsync(token).ConfigureAwait(true);
            WinRmText = Localizer[L.RemoteActions.WinRmAvailable];
            var release = os.BuildNumber is { } build && WindowsReleaseCatalog.EditionOf(os.Caption) is { } edition ? _catalog.Find(build, edition)?.Release : null;
            SystemText = release is null ? $"{os.Caption} ({os.FullBuild})" : $"{os.Caption} – {release} ({os.FullBuild})";
            LastBootText = os.LastBoot is { } boot ? Moment(boot) : string.Empty;
            return Localizer[L.RemoteActions.StatusRead];
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCall))]
    private Task LoadUsersAsync() => CallAsync(L.RemoteActions.ActionUsers, RemoteScripts.LoggedOnUsers, auditAction: null, async (remote, token) =>
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
    private Task LoadServicesAsync() => CallAsync(L.RemoteActions.ActionServices, RemoteScripts.Services, auditAction: null, async (remote, token) =>
    {
        _allServices = await remote.GetServicesAsync(token).ConfigureAwait(true);
        FillServices();
        return Localizer.Format(L.RemoteActions.ServicesRead, _allServices.Count, _allServices.Count(s => s.IsAutomaticAndStopped));
    });

    /// <summary>"Odśwież zasady grupy": gpupdate /force for the computer (and the users when the setting allows it).</summary>
    [RelayCommand(CanExecute = nameof(CanCall))]
    private Task RefreshPolicyAsync() => CallAsync(L.RemoteActions.ActionRefreshPolicy, RemoteScripts.RefreshPolicy, "Remote.RefreshPolicy", async (remote, token) =>
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
        await CallAsync(L.RemoteActions.ActionRestart, script, "Remote.Restart", async (remote, token) =>
        {
            await remote.ScheduleRestartAsync(request.Delay, request.Message, request.Force, token).ConfigureAwait(true);
            return request.Delay == TimeSpan.Zero
                ? Localizer.Format(L.RemoteActions.RestartScheduledNow, target.Name)
                : Localizer.Format(L.RemoteActions.RestartScheduled, target.Name, (int)request.Delay.TotalMinutes);
        }, newValue).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCall))]
    private Task AbortRestartAsync() => CallAsync(L.RemoteActions.ActionAbortRestart, RemoteScripts.AbortRestart, "Remote.AbortRestart", async (remote, token) =>
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

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _calls.Cancel();

    [RelayCommand]
    private void Close() => RequestClose(true);

    private bool HasTarget() => Target is not null && !IsSessionEnded;

    private bool CanCall() => HasTarget() && _remote is not null && !IsBusy;

    private bool CanStop() => IsBusy;

    // ---- Lifetime ------------------------------------------------------------------------------------------------------

    protected override void OnSessionEnded()
    {
        _calls.Cancel();
        NotifyCommands();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(UsersNote));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _calls.Cancel();
            _closed.Cancel();
            _closed.Dispose();
        }
    }

    partial void OnShowOnlyStoppedAutomaticChanged(bool value) => FillServices();

    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    // ---- Helpers -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One remote action: a CIM session (the window's credential, Kerberos), the call, the log line; audited when
    /// <paramref name="auditAction"/> is set; its PowerShell equivalent recorded after a success.
    /// </summary>
    private async Task CallAsync(string titleKey, string command, string? auditAction, Func<IRemoteComputerSession, CancellationToken, Task<string>> call, string? auditValue = null)
    {
        if (Target is not { } target || _remote is not { } remote || Session is not { } session)
        {
            return;
        }

        var options = Options();
        var token = _calls.Next();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _closed.Token, Services.Connection?.Lifetime ?? CancellationToken.None);
        IsBusy = true;
        Error = null;
        var title = Localizer[titleKey];
        string? error = null;
        try
        {
            await using var remoteSession = await remote.ConnectAsync(target, session, options, linked.Token).ConfigureAwait(true);
            var text = await call(remoteSession, linked.Token).ConfigureAwait(true);
            AddLog($"{title}: {text}", isError: false);
            var explicitCredential = session.Info.Authentication != AuthenticationMode.CurrentUser;
            _powerShell.RecordScript(title, target.DnsHostName, RemoteScripts.Build(target.DnsHostName, options, explicitCredential, command), session.Info.DomainDnsName, Services.Time.GetUtcNow());
        }
        catch (RemoteActionException ex)
        {
            error = RemoteText.Error(Localizer, ex.Kind);
            if (ex.Kind == RemoteErrorKind.WinRmNotAvailable)
            {
                WinRmText = Localizer[L.RemoteActions.WinRmUnavailable];
            }
        }
        catch (OperationCanceledException)
        {
            error = RemoteText.Error(Localizer, RemoteErrorKind.Cancelled);
        }
        finally
        {
            if (!IsDisposed)
            {
                IsBusy = false;
            }
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
