using System.Globalization;
using AdMng.Core.PowerShell;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.PowerShell;
using static AdMng.Presentation.Tests.PowerShell.FakePowerShellSession;
using static AdMng.Presentation.Tests.PowerShell.PowerShellWindowTestSupport;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>
/// Turning the trusted mode off (audit PS-02): the session goes on in a new runspace, so the window has a state of its own while it is
/// made - nothing runs, nothing completes, the switch waits - says in the scrollback that the variables and functions are gone, keeps
/// its history and output, asks the new runspace about the lockdown, and when the new runspace cannot be made it says that PowerShell
/// could not be started instead of staying in the trusted mode.
/// </summary>
public sealed class PowerShellWindowTrustedModeTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(PowerShellHarness Harness, PowerShellWindowViewModel Window)> TrustedAsync(CultureInfo? culture = null)
    {
        var harness = await PowerShellHarness.CreateAsync(culture);
        var window = await harness.OpenAsync();
        harness.Directory.Dialogs.ConfirmAnswers.Enqueue(true);
        await window.ToggleTrustedModeCommand.ExecuteAsync(null);
        Assert.True(window.IsTrustedMode);
        return (harness, window);
    }

    [Fact]
    public async Task WhileTheTrustedModeIsTurnedOff_TheLineSaysSo_AndNothingRunsCompletesOrSwitches()
    {
        var (harness, window) = await TrustedAsync();
        using var _ = window;
        harness.Session.Queue(Text("x\n"));
        await PowerShellHarness.RunAsync(window, "$x = 'trusted'");
        harness.Session.NewRunspaceHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Session.Completion = (_, _) => new PowerShellCompletionResult(0, 3, [new("Get-Date", "Get-Date", PowerShellCompletionKind.Command, null)]);
        var seen = harness.Record(window);

        var switching = window.ToggleTrustedModeCommand.ExecuteAsync(null);
        await harness.Session.NewRunspaceStarted.Task.WaitAsync(Limit, Ct);
        PowerShellHarness.Type(window, "Get");
        await window.Input.CompleteAsync(Ct);

        Assert.True(window.IsSwitchingMode);
        Assert.False(window.IsReady);
        Assert.Equal(PageStatusKind.Working, window.Status.Current.Kind);
        Assert.Equal("Turning the trusted mode off: starting a new PowerShell session…", window.Status.Current.Message);
        Assert.Null(window.Status.Current.Action);
        Assert.False(window.RunCommand.CanExecute(null));
        Assert.False(window.ToggleTrustedModeCommand.CanExecute(null));
        Assert.False(window.CanToggleTrustedMode);
        Assert.Equal("The trusted mode is being turned off.", window.TrustedModeDisabledReason);
        Assert.Empty(harness.Session.CompletionRequests);
        Assert.False(window.Input.IsCompletionOpen);

        harness.Session.NewRunspaceHold.SetResult();
        await switching.WaitAsync(Limit, Ct);

        Assert.False(window.IsSwitchingMode);
        Assert.False(window.IsTrustedMode);
        Assert.True(window.IsReady);
        Assert.True(window.RunCommand.CanExecute(null));
        Assert.True(window.CanToggleTrustedMode);
        Assert.Null(window.TrustedModeDisabledReason);
        Assert.Equal(1, harness.Session.NewRunspaces);
        Assert.Equal(
            (PowerShellLineKind.Information, "Trusted mode is off: ConstrainedLanguage, in a new PowerShell session. The variables, functions and aliases of earlier commands are gone; the history and the output stay."),
            (window.Output.Lines[^1].Kind, window.Output.Lines[^1].Text));
        Assert.Contains(window.Output.Lines, line => line.Text == "PS> $x = 'trusted'");
        Assert.Equal(["$x = 'trusted'"], window.History.Entries);
        Assert.Equal("Get", window.Input.Text);
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
        Assert.EndsWith(" · ConstrainedLanguage", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.Equal(PageStatusKind.Working, seen.Plain[0].Kind);
        Assert.DoesNotContain("trusted mode", window.WindowTitle, StringComparison.Ordinal);

        // The same session object goes on: it made its own new runspace (no second session from the factory).
        Assert.Single(harness.Factory.Requests);
        Assert.Equal(0, harness.Session.Disposals);
    }

    [Fact]
    public async Task TurningTheTrustedModeOff_ClosesTheCompletionList()
    {
        var (harness, window) = await TrustedAsync();
        using var _ = window;
        harness.Session.Completion = (_, _) => new PowerShellCompletionResult(0, 3, [new("Get-Date", "Get-Date", PowerShellCompletionKind.Command, null), new("Get-Item", "Get-Item", PowerShellCompletionKind.Command, null)]);
        PowerShellHarness.Type(window, "Get");
        await window.Input.CompleteAsync(Ct);
        Assert.True(window.Input.IsCompletionOpen);

        await window.ToggleTrustedModeCommand.ExecuteAsync(null);

        Assert.False(window.Input.IsCompletionOpen);
        Assert.Equal("Get", window.Input.Text);
        Assert.False(window.IsTrustedMode);
    }

    /// <summary>A highlight that runs on its pool thread when the switch starts is dropped, and the text is highlighted again after it.</summary>
    [Fact]
    public async Task TurningTheTrustedModeOff_StopsAHighlightThatRuns_AndHighlightsTheTextAgainAfterwards()
    {
        var (harness, window) = await TrustedAsync();
        using var _ = window;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        harness.Session.TokenizeHook = text =>
        {
            if (text.Length > PowerShellInputModel.SynchronousHighlightLength && Interlocked.Increment(ref held) == 1)
            {
                started.TrySetResult();
                hold.Task.GetAwaiter().GetResult();
            }
        };
        var longText = "Get-AdmUser " + new string('x', PowerShellInputModel.SynchronousHighlightLength);
        window.Input.SetText(longText, longText.Length);
        var first = window.Input.HighlightTask;
        await started.Task.WaitAsync(Limit, Ct);
        harness.Session.NewRunspaceHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var switching = window.ToggleTrustedModeCommand.ExecuteAsync(null);
        await harness.Session.NewRunspaceStarted.Task.WaitAsync(Limit, Ct);
        hold.SetResult();
        await first.WaitAsync(Limit, Ct);
        Assert.Empty(window.Input.Spans);

        harness.Session.NewRunspaceHold.SetResult();
        await switching.WaitAsync(Limit, Ct);
        await window.Input.HighlightTask.WaitAsync(Limit, Ct);

        Assert.Equal(PowerShellSyntaxKind.Command, window.Input.Spans[0].Kind);
        Assert.Equal(longText, window.Input.Text);
    }

    [Fact]
    public async Task TheConfirmationOfTheTrustedMode_SaysThatTurningItOffStartsANewSession()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Directory.Dialogs.ConfirmAnswers.Enqueue(false);

        await window.ToggleTrustedModeCommand.ExecuteAsync(null);

        var question = Assert.Single(harness.Directory.Dialogs.Shown, shown => shown.StartsWith("confirm:", StringComparison.Ordinal));
        Assert.Contains("Turning it off starts a new PowerShell session in the window: the variables, functions and aliases defined until then are gone.", question, StringComparison.Ordinal);
    }

    /// <summary>
    /// Audit DLR-9 and PS-TRUSTED-CRED-TEXT: the question and the note of the trusted mode named "the audit log and the rollback"
    /// ("dziennika audytu i wycofywania"), which the program calls the session log and Undo ("dziennik sesji", "cofanie"), and did not
    /// say that the code of the mode can reach the window's directory session and its credentials (ADR 0010, decision 1).
    /// </summary>
    [Theory]
    [InlineData(
        "en-US",
        "In trusted mode a script can run any .NET code in the process of AD Manager: it can reach the program's memory, the window's directory session and its credentials included, and change the directory directly, around the change plan – without the preview, the session log and Undo of AD Manager. It is a protection against mistakes, not a security boundary.",
        "Trusted mode is on: scripts in this window can run any .NET code, reach the window's directory session and its credentials, and change the directory around the change plan, without the preview, the session log and Undo of AD Manager. The switch turns it off.")]
    [InlineData(
        "pl-PL",
        "W trybie zaufanym skrypt może uruchamiać dowolny kod .NET w procesie programu: może sięgnąć do jego pamięci, także do sesji katalogu okna i jej poświadczeń, i zmieniać katalog bezpośrednio, z pominięciem planu zmian – bez podglądu, dziennika sesji i cofania zmian w AD Manager. To zabezpieczenie przed pomyłkami, a nie granica bezpieczeństwa.",
        "Tryb zaufany jest włączony: skrypty w tym oknie mogą uruchamiać dowolny kod .NET, sięgać do sesji katalogu okna i jej poświadczeń oraz zmieniać katalog z pominięciem planu zmian, bez podglądu, dziennika sesji i cofania zmian w AD Manager. Przełącznik wyłącza ten tryb.")]
    public async Task TheQuestionAndTheNoteOfTheTrustedMode_SayThatItsCodeReachesTheCredentials_InTheTermsOfTheProgram(string culture, string question, string note)
    {
        var harness = await PowerShellHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var window = await harness.OpenAsync();
        harness.Directory.Dialogs.ConfirmAnswers.Enqueue(true);

        await window.ToggleTrustedModeCommand.ExecuteAsync(null);

        var asked = Assert.Single(harness.Directory.Dialogs.Shown, shown => shown.StartsWith("confirm:", StringComparison.Ordinal));
        Assert.Contains(question, asked, StringComparison.Ordinal);
        Assert.True(window.IsTrustedMode);
        Assert.Equal(note, window.Status.Current.Message);
        foreach (var old in new[] { "audit", "rollback", "wycofywania" })
        {
            Assert.DoesNotContain(old, asked + window.Status.Current.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AfterTheTrustedMode_TheLockdownIsAskedOfTheNewRunspace()
    {
        var (harness, window) = await TrustedAsync();
        using var _ = window;
        harness.Session.WhileTheNewRunspaceIsMade = () => harness.Session.TrustedModeBlock = PowerShellTrustedModeBlock.SystemLockdown;

        await window.ToggleTrustedModeCommand.ExecuteAsync(null);

        Assert.False(window.IsTrustedMode);
        Assert.False(window.CanToggleTrustedMode);
        Assert.Equal("A system policy (WDAC or AppLocker) enforces ConstrainedLanguage mode.", window.TrustedModeDisabledReason);
        Assert.StartsWith("A system policy (WDAC or AppLocker) enforces ConstrainedLanguage mode: the trusted mode is not available", window.Output.Lines[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheNewRunspaceCannotBeMade_PowerShellCouldNotBeStarted_AndTheWindowIsNotTrustedAnyMore()
    {
        var (harness, window) = await TrustedAsync();
        using var _ = window;
        harness.Session.NewRunspaceFailure = new PowerShellSessionUnavailableException(
            PowerShellSessionUnavailableException.DefaultMessage, new InvalidOperationException("The engine's files are missing."));

        await window.ToggleTrustedModeCommand.ExecuteAsync(null);

        const string Expected = "PowerShell could not be started: Unexpected error: The engine's files are missing. Close the window and open it again.";
        Assert.False(window.IsTrustedMode);
        Assert.False(window.IsReady);
        Assert.False(window.IsSwitchingMode);
        Assert.DoesNotContain("trusted mode", window.WindowTitle, StringComparison.Ordinal);
        Assert.Equal(PageStatusKind.Error, window.Status.Current.Kind);
        Assert.Equal(Expected, window.Status.Current.Message);
        Assert.False(window.Status.Current.CanClose);
        Assert.DoesNotContain("missing", window.Status.Current.Log.ToString(), StringComparison.Ordinal);
        Assert.Equal((PowerShellLineKind.Information, Expected), (window.Output.Lines[^1].Kind, window.Output.Lines[^1].Text));
        Assert.False(window.CanToggleTrustedMode);
        Assert.Equal("PowerShell could not be started in this window.", window.TrustedModeDisabledReason);
        PowerShellHarness.Type(window, "Get-Date");
        Assert.False(window.RunCommand.CanExecute(null));
        await harness.Session.Disposed.WaitAsync(Limit, Ct);

        harness.Directory.Localizer.SetCulture(Polish);
        Assert.Equal("Nie można uruchomić PowerShell: Nieoczekiwany błąd: The engine's files are missing. Zamknij okno i otwórz je ponownie.", window.Status.Current.Message);
        Assert.Equal("W tym oknie nie można było uruchomić PowerShell.", window.TrustedModeDisabledReason);
    }

    /// <summary>A command after which the session has no runspace (a left command whose new runspace could not be made): that lasts.</summary>
    [Fact]
    public async Task ACommandOfASessionWithoutARunspace_MakesTheWindowSayPowerShellCouldNotBeStarted()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.Lost = new PowerShellSessionUnavailableException(
            PowerShellSessionUnavailableException.DefaultMessage, new InvalidOperationException("The engine's files are missing."));

        await PowerShellHarness.RunAsync(window, "Get-Date");

        Assert.False(window.IsReady);
        Assert.False(window.IsRunning);
        Assert.Equal(PageStatusKind.Error, window.Status.Current.Kind);
        Assert.Equal("PowerShell could not be started: Unexpected error: The engine's files are missing. Close the window and open it again.", window.Status.Current.Message);
        Assert.Equal(window.Status.Current.Message, window.Output.Lines[^1].Text);
        Assert.Equal("PowerShell could not be started in this window.", window.TrustedModeDisabledReason);
        await harness.Session.Disposed.WaitAsync(Limit, Ct);
    }

    [Fact]
    public async Task AWindowClosedWhileTheTrustedModeIsTurnedOff_EndsQuietly_AndClosesTheSession()
    {
        var (harness, window) = await TrustedAsync();
        harness.Session.NewRunspaceHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var switching = window.ToggleTrustedModeCommand.ExecuteAsync(null);
        await harness.Session.NewRunspaceStarted.Task.WaitAsync(Limit, Ct);

        window.Dispose();
        harness.Session.NewRunspaceHold.SetResult();
        await switching.WaitAsync(Limit, Ct);

        await harness.Session.Disposed.WaitAsync(Limit, Ct);
        Assert.Equal(1, harness.Session.Disposals);
        Assert.False(window.IsReady);
    }

    [Fact]
    public async Task InPolish_TheSwitchOffSaysSoInPolish()
    {
        var (harness, window) = await TrustedAsync(Polish);
        using var _ = window;
        harness.Session.NewRunspaceHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var switching = window.ToggleTrustedModeCommand.ExecuteAsync(null);
        await harness.Session.NewRunspaceStarted.Task.WaitAsync(Limit, Ct);
        Assert.Equal("Wyłączanie trybu zaufanego: uruchamianie nowej sesji PowerShell…", window.Status.Current.Message);
        Assert.Equal("Tryb zaufany jest właśnie wyłączany.", window.TrustedModeDisabledReason);
        harness.Session.NewRunspaceHold.SetResult();
        await switching.WaitAsync(Limit, Ct);

        Assert.StartsWith("Tryb zaufany jest wyłączony: ConstrainedLanguage, w nowej sesji PowerShell.", window.Output.Lines[^1].Text, StringComparison.Ordinal);
    }
}
