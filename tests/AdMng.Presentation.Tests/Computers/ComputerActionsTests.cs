using AdMng.Core.Batch;
using AdMng.Core.Computers.Remote;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.Tests.Computers;

/// <summary>Reset of computer accounts, ping and the local tools (K1.3, K1.4) – shared by the shell, the review and the property sheet.</summary>
public class ComputerActionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ResetAccount_AsksForTheDomainTyped_AndSetsARandomMachinePassword()
    {
        var scenario = await ComputerScenario.CreateAsync();
        var harness = scenario.Harness;
        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);

        var result = await scenario.Actions.ResetAccountsAsync(harness.Session, [scenario.Sample.Pro24H2, scenario.Sample.Dc02, scenario.Company.JanKowalski], Ct);

        var (title, message, expected) = Assert.Single(harness.Dialogs.TypedConfirmations);
        Assert.Equal("Reset Account", title);
        Assert.Equal("contoso.test", expected);
        Assert.StartsWith("Reset the computer account KRK-PC101 in the domain contoso.test?", message, StringComparison.Ordinal);
        Assert.Contains("Not reset (domain controllers or other objects): DC02, Jan Kowalski.", message, StringComparison.Ordinal);
        Assert.Contains("Test-ComputerSecureChannel -Repair", message, StringComparison.Ordinal);
        Assert.NotNull(result);
        Assert.Equal(1, result.SucceededCount);
        var reset = Assert.Single(harness.Session.PasswordResets);
        Assert.Equal(scenario.Sample.Pro24H2, reset.DistinguishedName);
        Assert.Equal(ResetComputerAccountOperation.GeneratedLength, reset.Password.Length);
        Assert.Equal((NotificationSeverity.Success, "Computer accounts reset: 1, failed: 0."), harness.Notifications.Items[^1]);

        // Audited without the password; never offered as a report with secrets, never in the batch editor.
        var audit = Assert.Single(harness.AuditLog.Entries, e => e.DistinguishedName == scenario.Sample.Pro24H2);
        Assert.DoesNotContain(reset.Password, audit.ToString(), StringComparison.Ordinal);
        Assert.False(BatchReportExporter.ContainsSecrets(result));
        Assert.Empty(harness.Dialogs.BatchEditorRequests);
        Assert.DoesNotContain(harness.PowerShell.Entries, e => e.Script.Contains(reset.Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetAccount_NotConfirmed_WritesNothing()
    {
        var scenario = await ComputerScenario.CreateAsync();
        var harness = scenario.Harness;
        harness.Dialogs.TypedConfirmAnswers.Enqueue(false);

        var result = await scenario.Actions.ResetAccountsAsync(harness.Session, [scenario.Sample.Pro24H2, scenario.Sample.Windows10], Ct);

        Assert.Null(result);
        Assert.StartsWith("Reset 2 computer accounts in the domain contoso.test?", Assert.Single(harness.Dialogs.TypedConfirmations).Message, StringComparison.Ordinal);
        Assert.Empty(harness.Session.PasswordResets);
        Assert.Empty(harness.AuditLog.Entries);
    }

    [Fact]
    public async Task ResetAccount_OfMany_NamesTheAccountsInTheConfirmation()
    {
        var scenario = await ComputerScenario.CreateAsync("pl-PL");
        var harness = scenario.Harness;
        var many = Enumerable.Range(1, ComputerActions.MaxListedResetTargets + 5)
            .Select(i => harness.Builder.AddComputer(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ŁÓDŹ-PC{i:000}"), scenario.Company.ComputersOu))
            .ToList();
        harness.Dialogs.TypedConfirmAnswers.Enqueue(false);

        var result = await scenario.Actions.ResetAccountsAsync(harness.Session, [scenario.Sample.Pro24H2, scenario.Sample.Windows10], Ct);
        var tooMany = await scenario.Actions.ResetAccountsAsync(harness.Session, many, Ct);

        Assert.Null(result);
        Assert.Null(tooMany);
        var two = harness.Dialogs.TypedConfirmations[0].Message;
        Assert.Contains("Konta: KRK-PC101, ", two, StringComparison.Ordinal);
        var listed = harness.Dialogs.TypedConfirmations[1].Message;
        Assert.StartsWith("Zresetować 25 kont komputerów w domenie contoso.test?", listed, StringComparison.Ordinal);
        Assert.Contains("Konta: ŁÓDŹ-PC001, ŁÓDŹ-PC002, ", listed, StringComparison.Ordinal);
        Assert.Contains("ŁÓDŹ-PC020 i jeszcze 5.", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("ŁÓDŹ-PC021", listed, StringComparison.Ordinal);
        Assert.Empty(harness.Session.PasswordResets);
    }

    [Fact]
    public async Task ResetAccount_OfDomainControllersOnly_IsRefusedWithTheReason()
    {
        var scenario = await ComputerScenario.CreateAsync("pl-PL");
        var harness = scenario.Harness;

        var result = await scenario.Actions.ResetAccountsAsync(harness.Session, [scenario.Sample.Dc02, scenario.Sample.Rodc], Ct);

        Assert.Null(result);
        Assert.Empty(harness.Dialogs.TypedConfirmations);
        var (title, error) = Assert.Single(harness.Dialogs.Errors);
        Assert.Equal("Resetuj konto", title);
        Assert.Contains("kontroler", error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Session.PasswordResets);
    }

    [Fact]
    public async Task ResetAccount_OfAUser_SaysItIsNotAComputer()
    {
        var scenario = await ComputerScenario.CreateAsync();
        var harness = scenario.Harness;

        var result = await scenario.Actions.ResetAccountsAsync(harness.Session, [scenario.Company.JanKowalski], Ct);

        Assert.Null(result);
        Assert.Equal("The object is not a computer (any more).", Assert.Single(harness.Dialogs.Errors).Message);
        Assert.Empty(harness.Dialogs.TypedConfirmations);
    }

    [Fact]
    public async Task Ping_ShowsTheResultAsANotification()
    {
        var scenario = await ComputerScenario.CreateAsync("pl-PL");
        var harness = scenario.Harness;
        scenario.Ping.Answering["srv-pliki01.contoso.test"] = TimeSpan.FromMilliseconds(2.4);

        var answered = await scenario.Actions.PingAsync(harness.Session, scenario.Sample.FileServer, Ct);
        var silent = await scenario.Actions.PingAsync(harness.Session, scenario.Sample.AppServer, Ct);

        Assert.True(answered!.Succeeded);
        Assert.False(silent!.Succeeded);
        Assert.Equal((NotificationSeverity.Success, "Ping srv-pliki01.contoso.test (10.10.1.15): 4 z 4 odpowiedzi, średnio 2 ms."), harness.Notifications.Items[^2]);
        Assert.Equal((NotificationSeverity.Warning, "Ping srv-app02.contoso.test: brak odpowiedzi."), harness.Notifications.Items[^1]);
    }

    [Fact]
    public async Task Tools_WithoutTheWindowsHost_AreNotAvailable()
    {
        var scenario = await ComputerScenario.CreateAsync();
        var harness = scenario.Harness;
        var actions = new ComputerActions(scenario.Services, harness.Planner);

        Assert.False(actions.CanLaunchTools);
        await actions.LaunchAsync(harness.Session, LocalTool.ComputerManagement, scenario.Sample.FileServer, Ct);
        await actions.PingAsync(harness.Session, scenario.Sample.FileServer, Ct);

        Assert.Equal(2, harness.Dialogs.Errors.Count);
        Assert.All(harness.Dialogs.Errors, e => Assert.Equal("This action is available only in AD Manager on Windows.", e.Message));
    }

    [Fact]
    public async Task Launch_Failure_ShowsTheErrorKind()
    {
        var scenario = await ComputerScenario.CreateAsync();
        var harness = scenario.Harness;
        scenario.Launcher.FailNext = RemoteErrorKind.NotSupported;

        await scenario.Actions.LaunchAsync(harness.Session, LocalTool.RemoteDesktop, scenario.Sample.FileServer, Ct);

        var (title, error) = Assert.Single(harness.Dialogs.Errors);
        Assert.Equal("Remote Desktop", title);
        Assert.StartsWith("Not possible for this computer or connection.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Target_OfANonComputer_IsRefused()
    {
        var scenario = await ComputerScenario.CreateAsync();
        var harness = scenario.Harness;

        var target = await scenario.Actions.ResolveTargetAsync(harness.Session, scenario.Company.JanKowalski, "Ping", Ct);

        Assert.Null(target);
        Assert.Equal("The object is not a computer (any more).", Assert.Single(harness.Dialogs.Errors).Message);
    }

    [Fact]
    public async Task RunAsync_DispatchesTheShellActions()
    {
        var scenario = await ComputerScenario.CreateAsync();
        scenario.Ping.Answering["krk-pc101.contoso.test"] = TimeSpan.FromMilliseconds(1);

        await scenario.Actions.RunAsync(ComputerAction.RemoteActions, [scenario.Sample.Pro24H2], Ct);
        await scenario.Actions.RunAsync(ComputerAction.Ping, [scenario.Sample.Pro24H2], Ct);
        await scenario.Actions.RunAsync(ComputerAction.EventViewer, [scenario.Sample.Pro24H2], Ct);

        Assert.Equal([scenario.Sample.Pro24H2], scenario.Dialogs.RemoteWindows);
        Assert.Equal(["krk-pc101.contoso.test"], scenario.Ping.Pinged);
        Assert.Equal(LocalTool.EventViewer, Assert.Single(scenario.Launcher.Launches).Tool);
    }
}
