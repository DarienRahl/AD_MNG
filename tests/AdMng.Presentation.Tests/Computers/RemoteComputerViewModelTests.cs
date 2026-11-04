using System.Globalization;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Localization;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Computers;

/// <summary>
/// The remote actions window (K1.4) over the fake WinRM client: every action, the restart dialog's typed confirmation
/// and verbatim message, error texts, cancellation on close, the session log and the PowerShell history.
/// </summary>
public class RemoteComputerViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(ComputerScenario Scenario, RemoteComputerViewModel Window, FakeRemoteComputer Computer)> OpenAsync(
        Func<ComputerInventorySample, string>? computer = null, string culture = "en-US")
    {
        var scenario = await ComputerScenario.CreateAsync(culture);
        var dn = (computer ?? (s => s.Pro24H2))(scenario.Sample);
        var host = scenario.Harness.Value(dn, Core.ActiveDirectory.Attr.DnsHostName)!;
        var remote = new FakeRemoteComputer
        {
            OperatingSystem = new RemoteOperatingSystem("Microsoft Windows 11 Pro", "10.0.26100", 26100, 4652, new DateTimeOffset(2026, 10, 26, 7, 15, 0, TimeSpan.Zero)),
        };
        remote.Users.Add(new LoggedOnUser(@"CONTOSO\jan.kowalski", 2, new DateTimeOffset(2026, 10, 27, 7, 30, 0, TimeSpan.Zero), false));
        remote.Users.Add(new LoggedOnUser(@"CONTOSO\zaneta.szymanska", 1, new DateTimeOffset(2026, 10, 27, 6, 55, 0, TimeSpan.Zero), true));
        remote.Services.Add(new RemoteService("Spooler", "Print Spooler", "Running", "Auto", false, "LocalSystem"));
        remote.Services.Add(new RemoteService("wuauserv", "Windows Update", "Stopped", "Manual", false, "LocalSystem"));
        remote.Services.Add(new RemoteService("BITS", "Background Intelligent Transfer Service", "Stopped", "Auto", true, "LocalSystem"));
        scenario.Remote.Computers[host] = remote;
        var window = scenario.Factory.CreateRemote(dn);
        await window.InitializeAsync();
        return (scenario, window, remote);
    }

    [Fact]
    public async Task Initialize_ResolvesTheTargetFromTheDnsHostName()
    {
        var (_, window, _) = await OpenAsync();
        using var _window = window;

        Assert.Equal("Remote actions – KRK-PC101", window.WindowTitle);
        Assert.Equal("krk-pc101.contoso.test", window.Target!.DnsHostName);
        Assert.Equal(@"Connection: WinRM (HTTP 5985, Kerberos) as CONTOSO\Administrator", window.ConnectionText);
        Assert.Null(window.Error);
        Assert.True(window.RefreshStatusCommand.CanExecute(null));
        Assert.Equal("tools:remote:" + AdMng.Presentation.Lists.DnKey.For(window.DistinguishedName), RemoteComputerViewModel.WindowKey(window.DistinguishedName));
    }

    [Fact]
    public async Task Initialize_WithoutDnsHostName_RefusesEveryAction()
    {
        var scenario = await ComputerScenario.CreateAsync();
        using var window = scenario.Factory.CreateRemote(scenario.Sample.Prestaged);
        await window.InitializeAsync();

        Assert.Null(window.Target);
        Assert.StartsWith("PREST-PC201 has no DNS host name in Active Directory", window.Error, StringComparison.Ordinal);
        Assert.False(window.RefreshStatusCommand.CanExecute(null));
        Assert.False(window.RemoteDesktopCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshStatus_PingsAndReadsTheSystemWithItsRelease()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _window = window;
        scenario.Ping.Answering["krk-pc101.contoso.test"] = TimeSpan.FromMilliseconds(4);

        await window.RefreshStatusCommand.ExecuteAsync(null);

        Assert.Equal("Ping krk-pc101.contoso.test (10.10.1.15): 4 of 4 replies, average 4 ms.", window.PingText);
        Assert.Equal("available", window.WinRmText);
        Assert.Equal("Microsoft Windows 11 Pro – Windows 11 24H2 (10.0.26100.4652)", window.SystemText);
        Assert.Equal(new DateTime(2026, 10, 26, 7, 15, 0).ToString("g", CultureInfo.GetCultureInfo("en-US")), window.LastBootText);
        Assert.Equal("Status: status read", window.Log[0].Text);
        Assert.Equal(["Connect", "OperatingSystem", "Dispose"], scenario.Remote.Calls.Select(c => c.Operation));
        var connect = scenario.Remote.Calls[0];
        Assert.Equal("dc01.contoso.test", connect.Server);
        Assert.Equal(TimeSpan.FromSeconds(30), connect.Options!.Timeout);

        // Reads are not audited; the PowerShell equivalent is recorded.
        Assert.Empty(scenario.Harness.AuditLog.Entries);
        var script = scenario.Harness.PowerShell.Entries[0];
        Assert.Equal("krk-pc101.contoso.test", script.Server);
        Assert.Contains("New-CimSession -ComputerName 'krk-pc101.contoso.test' -Authentication Kerberos", script.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Credential", script.Script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadUsersAndServices_ListTheConsoleFirst_AndFilterStoppedAutomaticServices()
    {
        var (_, window, _) = await OpenAsync();
        using var _window = window;

        await window.LoadUsersCommand.ExecuteAsync(null);
        await window.LoadServicesCommand.ExecuteAsync(null);

        Assert.Equal([@"CONTOSO\zaneta.szymanska", @"CONTOSO\jan.kowalski"], window.LoggedOnUsers.Select(u => u.Account));
        Assert.Equal("console", window.LoggedOnUsers[0].Session);
        Assert.Equal("2", window.LoggedOnUsers[1].Session);
        Assert.Equal("Services: 3 services, 1 automatic and stopped", window.Log[0].Text);
        Assert.Equal(3, window.ServiceRows.Count);
        window.ShowOnlyStoppedAutomatic = true;
        var bits = Assert.Single(window.ServiceRows);
        Assert.Equal("Background Intelligent Transfer Service", bits.DisplayName);
        Assert.Equal("Auto (delayed)", bits.StartMode);
    }

    [Fact]
    public async Task RefreshPolicy_IsAudited_AndRefreshesTheUsersOnlyWhenTheSettingSaysSo()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _window = window;

        await window.RefreshPolicyCommand.ExecuteAsync(null);
        await scenario.Harness.Settings.UpdateAsync(s => s.Computers.RefreshUserPolicy = true, Ct);
        await window.RefreshPolicyCommand.ExecuteAsync(null);

        var refreshes = scenario.Remote.Calls.Where(c => c.Operation == "RefreshGroupPolicy").Select(c => c.Argument).ToList();
        Assert.Equal(["Computer", "ComputerAndUsers"], refreshes);
        Assert.StartsWith("Refresh Group Policy: policy refreshed (computer policy processed ", window.Log[1].Text, StringComparison.Ordinal);
        Assert.EndsWith("The users' policy refresh was scheduled.", window.Log[0].Text, StringComparison.Ordinal);
        var audit = scenario.Harness.AuditLog.Entries;
        Assert.Equal(2, audit.Count);
        Assert.All(audit, e =>
        {
            Assert.Equal("Remote.RefreshPolicy", e.Action);
            Assert.Equal("krk-pc101.contoso.test", e.Server);
            Assert.Equal(scenario.Sample.Pro24H2, e.DistinguishedName);
            Assert.True(e.Succeeded);
        });
    }

    [Fact]
    public async Task Restart_OfAWorkstation_PassesDelayAndMessageVerbatim_AndAuditsWithoutTheMessage()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _window = window;
        const string message = "Aktualizacja systemu – zapisz pracę! Restart za 15 min (\"pilne\").";
        RestartComputerViewModel? dialog = null;
        scenario.Dialogs.RestartHandler = d =>
        {
            dialog = d;
            Assert.False(d.NeedsTypedName);
            d.DelayMinutes = 15;
            d.Message = message;
            d.Force = true;
            d.ConfirmCommand.Execute(null);
            return true;
        };

        await window.RestartCommand.ExecuteAsync(null);

        Assert.NotNull(dialog);
        var restart = Assert.Single(scenario.Remote.Calls, c => c.Operation == "ScheduleRestart");
        Assert.Equal(TimeSpan.FromMinutes(15), restart.Delay);
        Assert.Equal(message, restart.Message);
        Assert.True(restart.Force);
        Assert.Equal("Restart…: KRK-PC101 restarts in 15 min", window.Log[0].Text);
        var audit = Assert.Single(scenario.Harness.AuditLog.Entries);
        Assert.Equal("Remote.Restart", audit.Action);
        Assert.Equal("delay=15 min; force=True", audit.NewValue);
        Assert.DoesNotContain("zapisz", audit.ToString(), StringComparison.Ordinal);
        var script = scenario.Harness.PowerShell.Entries[0].Script;
        Assert.Contains("Win32ShutdownTracker", script, StringComparison.Ordinal);
        Assert.Contains("Timeout = [uint32]900", script, StringComparison.Ordinal);
        Assert.Contains("Flags = 6", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_Cancelled_DoesNothing()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _window = window;
        scenario.Dialogs.RestartHandler = _ => false;

        await window.RestartCommand.ExecuteAsync(null);

        Assert.Empty(scenario.Remote.Calls);
        Assert.Empty(scenario.Harness.AuditLog.Entries);
        Assert.Empty(window.Log);
    }

    [Fact]
    public async Task Restart_OfAServer_NeedsItsTypedName()
    {
        var (scenario, window, _) = await OpenAsync(s => s.FileServer);
        using var _window = window;
        scenario.Dialogs.RestartHandler = d =>
        {
            Assert.True(d.NeedsTypedName);
            Assert.False(d.ConfirmCommand.CanExecute(null));
            Assert.Equal("Type the computer name SRV-PLIKI01 to confirm (server, domain controller or computer of unknown type).", d.Error);
            d.TypedName = "srv-pliki";
            Assert.False(d.ConfirmCommand.CanExecute(null));
            d.TypedName = "srv-pliki01 ";
            Assert.True(d.ConfirmCommand.CanExecute(null));
            d.ConfirmCommand.Execute(null);
            return true;
        };

        await window.RestartCommand.ExecuteAsync(null);

        var restart = Assert.Single(scenario.Remote.Calls, c => c.Operation == "ScheduleRestart");
        Assert.Equal(TimeSpan.FromMinutes(10), restart.Delay);
        Assert.Equal("The IT department will restart this computer in 10 min. Please save your work.", restart.Message);
        Assert.False(restart.Force);
    }

    [Fact]
    public async Task AbortRestart_IsAudited()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _window = window;

        await window.AbortRestartCommand.ExecuteAsync(null);

        // shutdown /a runs on the computer without an exit code to read: the log says it was requested, not that it worked.
        Assert.Equal(
            "Cancel scheduled restart: cancellation of a scheduled restart requested (shutdown /a; not confirmed – if no restart was pending, nothing changed)",
            window.Log[0].Text);
        Assert.Equal("Remote.AbortRestart", Assert.Single(scenario.Harness.AuditLog.Entries).Action);
    }

    [Theory]
    [InlineData(RemoteErrorKind.Unreachable, "en-US", "The computer does not answer. Check that the computer is on, that its DNS name resolves and that the firewall lets you in.")]
    [InlineData(RemoteErrorKind.WinRmNotAvailable, "en-US", "WinRM is not available on the computer. Enable WinRM by Group Policy (service, listener, firewall rule) – see docs/KOMPUTERY.md.")]
    [InlineData(RemoteErrorKind.AuthenticationFailed, "en-US", "Kerberos authentication failed. Check the computer's SPN (HTTP/ or WSMAN/), the clocks and that the account is a domain account; NTLM is never used.")]
    [InlineData(RemoteErrorKind.AccessDenied, "en-US", "Access denied. The account of this window must be a local administrator of the computer (Domain Admins on domain controllers).")]
    [InlineData(RemoteErrorKind.Timeout, "en-US", "The computer did not answer in time. Raise the timeout in the settings (Computers) or try again.")]
    [InlineData(RemoteErrorKind.NotSupported, "en-US", "Not possible for this computer or connection. Remote actions need AD Manager on Windows, a DNS name of the computer and a Windows account or a typed domain account (not an LDAP simple bind).")]
    [InlineData(RemoteErrorKind.Failed, "en-US", "The remote operation failed. See the Windows event log of the computer.")]
    [InlineData(RemoteErrorKind.Unreachable, "pl-PL", "Komputer nie odpowiada. Sprawdź, czy komputer jest włączony, czy jego nazwa DNS się rozwiązuje i czy zapora go przepuszcza.")]
    [InlineData(RemoteErrorKind.WinRmNotAvailable, "pl-PL", "Usługa WinRM jest niedostępna na komputerze. Włącz WinRM zasadą grupy (usługa, nasłuchiwanie, reguła zapory) – zobacz docs/KOMPUTERY.md.")]
    [InlineData(RemoteErrorKind.AuthenticationFailed, "pl-PL", "Uwierzytelnienie Kerberos nie powiodło się. Sprawdź SPN komputera (HTTP/ lub WSMAN/), zegary i czy konto jest kontem domenowym; NTLM nie jest używany.")]
    [InlineData(RemoteErrorKind.AccessDenied, "pl-PL", "Odmowa dostępu. Konto tego okna musi być lokalnym administratorem komputera (Domain Admins na kontrolerach domeny).")]
    [InlineData(RemoteErrorKind.Timeout, "pl-PL", "Komputer nie odpowiedział na czas. Zwiększ limit czasu w ustawieniach (Komputery) albo spróbuj ponownie.")]
    [InlineData(RemoteErrorKind.NotSupported, "pl-PL", "Niemożliwe dla tego komputera lub połączenia. Zdalne akcje wymagają AD Manager w systemie Windows, nazwy DNS komputera oraz konta Windows lub wpisanego konta domenowego (nie prostego wiązania LDAP).")]
    [InlineData(RemoteErrorKind.Failed, "pl-PL", "Zdalna operacja nie powiodła się. Zajrzyj do dziennika zdarzeń Windows na komputerze.")]
    public async Task Errors_AreShownWithWhatToDo(RemoteErrorKind kind, string culture, string expected)
    {
        var (scenario, window, _) = await OpenAsync(culture: culture);
        using var _window = window;
        scenario.Remote.ConnectFailures["krk-pc101.contoso.test"] = kind;

        await window.LoadUsersCommand.ExecuteAsync(null);

        Assert.Equal(expected, window.Error);
        Assert.True(window.Log[0].IsError);
        Assert.EndsWith(expected, window.Log[0].Text, StringComparison.Ordinal);
        Assert.Empty(scenario.Harness.PowerShell.Entries);
        Assert.Equal(kind == RemoteErrorKind.WinRmNotAvailable ? (culture == "pl-PL" ? "niedostępne" : "not available") : string.Empty, window.WinRmText);
    }

    [Fact]
    public void EveryErrorKind_HasTextsInBothLanguages()
    {
        foreach (var culture in new[] { "en-US", "pl-PL" })
        {
            var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo(culture));
            foreach (var kind in Enum.GetValues<RemoteErrorKind>())
            {
                Assert.NotEqual(RemoteText.ErrorKey(kind), localizer[RemoteText.ErrorKey(kind)]);
                Assert.NotEqual(RemoteText.HintKey(kind), localizer[RemoteText.HintKey(kind)]);
            }
        }
    }

    [Fact]
    public async Task FailedRestart_IsAuditedAsFailed()
    {
        var (scenario, window, computer) = await OpenAsync();
        using var _window = window;
        computer.Failures["ScheduleRestart"] = RemoteErrorKind.AccessDenied;
        scenario.Dialogs.RestartHandler = d =>
        {
            d.ConfirmCommand.Execute(null);
            return true;
        };

        await window.RestartCommand.ExecuteAsync(null);

        var audit = Assert.Single(scenario.Harness.AuditLog.Entries);
        Assert.False(audit.Succeeded);
        Assert.StartsWith("Access denied.", audit.Error, StringComparison.Ordinal);
        Assert.Empty(scenario.Harness.PowerShell.Entries);
    }

    [Fact]
    public async Task ClosingTheWindow_CancelsTheRunningCall()
    {
        var (scenario, window, _) = await OpenAsync();
        scenario.Remote.HoldNextOperation = true;

        var call = window.LoadServicesCommand.ExecuteAsync(null);
        await scenario.Remote.Held.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(window.IsBusy);
        Assert.True(window.StopCommand.CanExecute(null));
        window.Dispose();
        await call.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal("Dispose", scenario.Remote.Calls[^1].Operation);
    }

    [Fact]
    public async Task RefreshStatus_IsBusyWhilePinging_AndAnotherActionNeitherStartsNorCancelsIt()
    {
        // Windows clients drop ICMP: the ping phase runs into its timeouts (up to 8 s).
        var (scenario, window, _) = await OpenAsync();
        using var _window = window;
        scenario.Ping.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var status = window.RefreshStatusCommand.ExecuteAsync(null);
        await scenario.Ping.Held.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(window.IsBusy);
        Assert.False(window.RestartCommand.CanExecute(null));
        Assert.False(window.LoadUsersCommand.CanExecute(null));
        Assert.True(window.StopCommand.CanExecute(null));

        // A call that slips through (a stale CanExecute) is refused instead of cancelling the running one.
        await window.LoadUsersCommand.ExecuteAsync(null);
        Assert.Empty(scenario.Remote.Calls);
        Assert.True(window.IsBusy);

        scenario.Ping.Hold.SetResult();
        await status.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(window.IsBusy);
        Assert.Null(window.Error);
        Assert.Equal("Status: status read", Assert.Single(window.Log).Text);
        Assert.Equal(["Connect", "OperatingSystem", "Dispose"], scenario.Remote.Calls.Select(c => c.Operation));
    }

    [Fact]
    public async Task Stop_EndsThePingPhaseOfTheStatusRefresh()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _window = window;
        scenario.Ping.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var status = window.RefreshStatusCommand.ExecuteAsync(null);
        await scenario.Ping.Held.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        window.StopCommand.Execute(null);
        await status.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.False(window.IsBusy);
        Assert.Empty(scenario.Remote.Calls);
        Assert.True(window.RestartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Stop_CancelsTheCall_AndSaysSo()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _window = window;
        scenario.Remote.HoldNextOperation = true;

        var call = window.LoadUsersCommand.ExecuteAsync(null);
        await scenario.Remote.Held.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        window.StopCommand.Execute(null);
        await call;

        Assert.False(window.IsBusy);
        Assert.Equal("Cancelled.", window.Error);
        Assert.Equal("Logged-on users: Cancelled.", window.Log[0].Text);
    }

    [Fact]
    public async Task Tools_StartWithoutCredentialPrompt_ForAWindowsAccount()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _window = window;

        await window.RemoteDesktopCommand.ExecuteAsync(null);
        await window.ComputerManagementCommand.ExecuteAsync(null);
        await window.EventViewerCommand.ExecuteAsync(null);

        Assert.Equal([LocalTool.RemoteDesktop, LocalTool.ComputerManagement, LocalTool.EventViewer], scenario.Launcher.Launches.Select(l => l.Tool));
        Assert.All(scenario.Launcher.Launches, l => Assert.False(l.WithWindowCredential));
        Assert.DoesNotContain(scenario.Harness.Dialogs.Shown, s => s.StartsWith("confirm:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tools_WithTheWindowsCredential_AskFirst_ButRemoteDesktopNeverGetsIt()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _window = window;
        var harness = scenario.Harness;
        harness.Session.Info = harness.Session.Info with { Authentication = AuthenticationMode.Credentials, BoundIdentity = @"CONTOSO\adm.kowalski" };

        await window.RemoteDesktopCommand.ExecuteAsync(null);
        await window.ComputerManagementCommand.ExecuteAsync(null);
        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await window.EventViewerCommand.ExecuteAsync(null);

        Assert.Equal(
            "confirm:The tool connects to krk-pc101.contoso.test as CONTOSO\\adm.kowalski – with the credentials of this connection window. They stay in the memory of the tool's logon session until you close it (never on disk). Start it?",
            harness.Dialogs.Shown[0]);
        Assert.Equal(2, harness.Dialogs.Shown.Count);
        Assert.Equal([LocalTool.RemoteDesktop, LocalTool.EventViewer], scenario.Launcher.Launches.Select(l => l.Tool));
        Assert.False(scenario.Launcher.Launches[0].WithWindowCredential);
        Assert.True(scenario.Launcher.Launches[1].WithWindowCredential);

        // The remote script then asks for the credential (never written into the script).
        await window.LoadUsersCommand.ExecuteAsync(null);
        Assert.Contains("-Credential (Get-Credential", harness.PowerShell.Entries[0].Script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionEnded_RefusesActions()
    {
        var (scenario, window, _) = await OpenAsync();
        using var _window = window;

        await scenario.Harness.ReconnectAsync(new FakeDirectoryBuilder(timeProvider: scenario.Harness.Time).Session);

        Assert.True(window.IsSessionEnded);
        Assert.False(window.RefreshStatusCommand.CanExecute(null));
        Assert.False(window.RestartCommand.CanExecute(null));
        Assert.False(window.RemoteDesktopCommand.CanExecute(null));
    }
}

/// <summary>The restart dialog on its own.</summary>
public class RestartComputerViewModelTests
{
    private static RemoteTarget Workstation => new("CN=KRK-PC101,OU=Komputery,OU=Firma,DC=contoso,DC=test", "KRK-PC101", "krk-pc101.contoso.test", Core.Computers.ComputerType.Workstation);

    private static RemoteTarget DomainController => new("CN=DC02,OU=Domain Controllers,DC=contoso,DC=test", "DC02", "dc02.contoso.test", Core.Computers.ComputerType.DomainController);

    [Fact]
    public async Task Defaults_ComeFromTheSettings_AndTheDefaultMessageFollowsTheDelay()
    {
        var scenario = await ComputerScenario.CreateAsync("pl-PL");
        using var dialog = scenario.Factory.CreateRestart(Workstation);

        Assert.Equal("Uruchom ponownie KRK-PC101", dialog.WindowTitle);
        Assert.Equal(10, dialog.DelayMinutes);
        Assert.Equal("Komputer zostanie ponownie uruchomiony przez dział IT za 10 min. Zapisz swoją pracę.", dialog.Message);
        Assert.Equal("KRK-PC101 uruchomi się ponownie za 10 min.", dialog.Summary);
        dialog.DelayMinutes = 0;
        Assert.Equal("Komputer zostanie ponownie uruchomiony przez dział IT za 0 min. Zapisz swoją pracę.", dialog.Message);
        Assert.Equal("KRK-PC101 uruchomi się ponownie natychmiast.", dialog.Summary);

        // A message of the operator is kept when the delay changes.
        dialog.Message = "Restart po aktualizacji – Łukasz";
        dialog.DelayMinutes = 30;
        Assert.Equal("Restart po aktualizacji – Łukasz", dialog.Message);
        Assert.Null(dialog.Error);
        Assert.Equal("32 / 500", dialog.MessageLength);
    }

    [Fact]
    public async Task Validation_DelayRange_MessageLength_TypedNameOfDomainControllers()
    {
        var scenario = await ComputerScenario.CreateAsync();
        using var dialog = scenario.Factory.CreateRestart(DomainController);

        Assert.True(dialog.NeedsTypedName);
        Assert.False(dialog.ConfirmCommand.CanExecute(null));
        dialog.TypedName = "DC02";
        Assert.True(dialog.ConfirmCommand.CanExecute(null));
        dialog.DelayMinutes = 1441;
        Assert.Equal("The delay must be 0–1440 minutes.", dialog.Error);
        dialog.DelayMinutes = -1;
        Assert.False(dialog.ConfirmCommand.CanExecute(null));
        dialog.DelayMinutes = 1440;
        dialog.Message = new string('x', 501);
        Assert.Equal("The message can have at most 500 characters.", dialog.Error);
        dialog.Message = new string('x', 500);
        Assert.Null(dialog.Error);

        dialog.ConfirmCommand.Execute(null);
        Assert.Equal(new RestartRequest(TimeSpan.FromMinutes(1440), new string('x', 500), false), dialog.Request);
    }

    [Theory]
    [InlineData(Core.Computers.ComputerType.Unknown, true)]
    [InlineData(Core.Computers.ComputerType.Server, true)]
    [InlineData(Core.Computers.ComputerType.ReadOnlyDomainController, true)]
    [InlineData(Core.Computers.ComputerType.Workstation, false)]
    public async Task TypedName_IsNeededForEverythingButAKnownWorkstation(Core.Computers.ComputerType type, bool needed)
    {
        // A cluster name object (no operatingSystem) restarts the node that owns it.
        var scenario = await ComputerScenario.CreateAsync("pl-PL");
        using var dialog = scenario.Factory.CreateRestart(new RemoteTarget("CN=KLASTER-ŁÓDŹ,OU=Serwery,OU=Firma,DC=contoso,DC=test", "KLASTER-LODZ", "klaster-lodz.contoso.test", type));

        Assert.Equal(needed, dialog.NeedsTypedName);
        Assert.Equal(!needed, dialog.ConfirmCommand.CanExecute(null));
        if (needed)
        {
            Assert.Equal("Wpisz nazwę komputera KLASTER-LODZ, aby potwierdzić (serwer, kontroler domeny lub komputer nieznanego typu).", dialog.Error);
            dialog.TypedName = "klaster-lodz";
            Assert.True(dialog.ConfirmCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task Cancel_GivesNoRequest()
    {
        var scenario = await ComputerScenario.CreateAsync();
        using var dialog = scenario.Factory.CreateRestart(Workstation);
        bool? closed = null;
        dialog.CloseRequested += (_, e) => closed = e;

        dialog.CancelCommand.Execute(null);

        Assert.False(closed);
        Assert.Null(dialog.Request);
    }
}
