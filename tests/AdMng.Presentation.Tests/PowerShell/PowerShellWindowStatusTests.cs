using System.Globalization;
using AdMng.Core.PowerShell;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.PowerShell;
using static AdMng.Presentation.Tests.PowerShell.FakePowerShellSession;
using static AdMng.Presentation.Tests.PowerShell.PowerShellWindowTestSupport;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>
/// What the PowerShell window says about itself (track R, stage R1): the title and the summary, starting the engine, the notes about
/// the policies that last before the first command, the trusted mode and its confirmation, a lockdown, a session that ended, an
/// engine that cannot start.
/// </summary>
public sealed class PowerShellWindowStatusTests
{
    private const string Directory = @"\\logs\transcripts";

    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static readonly PowerShellPolicyOrigin CoreComputer = new(PowerShellPolicyStore.PowerShellCore, PowerShellPolicyScope.Computer);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PowerShellPolicies Transcribing { get; } = new() { Transcription = new PowerShellTranscriptionPolicy(true, Directory, false, CoreComputer) };

    private static string Where(PowerShellHarness harness)
    {
        var info = harness.Directory.Session.Info;
        return $"{info.DomainDnsName} – {info.Server} – {info.BoundIdentity}";
    }

    // ---- Title and summary -----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTitle_NamesTheDomainTheDcAndTheAccount_AndTheTrustedMode()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Directory.Dialogs.ConfirmAnswers.Enqueue(true);

        Assert.Equal(Where(harness) + " – PowerShell", window.WindowTitle);
        await window.ToggleTrustedModeCommand.ExecuteAsync(null);
        Assert.Equal(Where(harness) + " – PowerShell – trusted mode", window.WindowTitle);

        harness.Directory.Localizer.SetCulture(Polish);
        Assert.Equal(Where(harness) + " – PowerShell – tryb zaufany", window.WindowTitle);
    }

    [Fact]
    public async Task TheSummary_IsTheDomainTheDcTheAccountAndTheMode_AndIsWhatTheLineSaysAtRest()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        var info = harness.Directory.Session.Info;

        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
        Assert.Equal($"{info.DomainDnsName} · {info.Server} · {info.BoundIdentity} · ConstrainedLanguage", window.Status.Current.Message);
    }

    // ---- Starting --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhileTheEngineStarts_TheLineSaysSo_AndNoCommandCanRun_ThenTheWindowIsReady()
    {
        var harness = await PowerShellHarness.CreateAsync();
        harness.Factory.Hold();
        using var window = harness.Create();
        var seen = harness.Record(window);

        var opening = window.InitializeAsync();
        await UntilAsync(window, () => window.IsStarting).WaitAsync(Limit, Ct);
        PowerShellHarness.Type(window, "Get-Date");

        Assert.False(window.IsReady);
        Assert.False(window.RunCommand.CanExecute(null));
        Assert.Equal("Starting PowerShell…", window.Status.Current.Message);
        Assert.Equal("PowerShell is not ready yet.", window.TrustedModeDisabledReason);
        Assert.False(window.Status.Current.Action is not null, "the start of the engine cannot be stopped");

        harness.Factory.Release();
        await opening.WaitAsync(Limit, Ct);

        Assert.True(window.IsReady);
        Assert.False(window.IsStarting);
        Assert.True(window.RunCommand.CanExecute(null));
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
        Assert.Equal([(PageStatusKind.Working, "Starting PowerShell…"), seen.Plain[^1]], seen.Plain);
    }

    [Fact]
    public async Task TheSession_IsCreatedOnceForTheWindow_WithItsDirectorySessionLanguageAndActions()
    {
        var harness = await PowerShellHarness.CreateAsync(Polish);
        using var window = harness.Create();

        await window.InitializeAsync();
        await window.InitializeAsync();

        var request = Assert.Single(harness.Factory.Requests);
        Assert.Same(harness.Directory.Session, request.Directory);
        Assert.Equal("pl-PL", request.Culture.Name);
        Assert.Same(harness.Actions, request.Actions);
        Assert.Equal(120, request.OutputWidth);
        Assert.False(string.IsNullOrWhiteSpace(request.ApplicationVersion));
        Assert.Equal("PowerShellSessionRequest", request.ToString());
    }

    [Fact]
    public async Task AnEngineThatCannotStart_IsAnErrorThatLasts_AndTheWindowRunsNothing()
    {
        var harness = await PowerShellHarness.CreateAsync();
        harness.Factory.Failure = new InvalidOperationException("The engine's files are missing.");
        using var window = await harness.OpenAsync();

        Assert.False(window.IsReady);
        Assert.Equal(PageStatusKind.Error, window.Status.Current.Kind);
        Assert.Equal("PowerShell could not be started: Unexpected error: The engine's files are missing. Close the window and open it again.", window.Status.Current.Message);
        Assert.False(window.Status.Current.CanClose);
        PowerShellHarness.Type(window, "Get-Date");
        Assert.False(window.RunCommand.CanExecute(null));
        Assert.DoesNotContain("missing", window.Status.Current.Log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Audit W-10: the reason of a start failure was the text of the start, in the language of the start.</summary>
    [Fact]
    public async Task AnEngineThatCannotStart_SaysSoInTheLanguageOfNow()
    {
        var harness = await PowerShellHarness.CreateAsync();
        harness.Factory.Failure = new UnauthorizedAccessException("Access to the engine's folder is denied.");
        using var window = await harness.OpenAsync();
        Assert.Equal("PowerShell could not be started: The file could not be accessed: Access to the engine's folder is denied. Close the window and open it again.", window.Status.Current.Message);

        harness.Directory.Localizer.SetCulture(Polish);

        Assert.Equal("Nie można uruchomić PowerShell: Nie można uzyskać dostępu do pliku: Access to the engine's folder is denied. Zamknij okno i otwórz je ponownie.", window.Status.Current.Message);
        Assert.Equal("W tym oknie nie można było uruchomić PowerShell.", window.TrustedModeDisabledReason);
    }

    /// <summary>Audit W-10: a build without the engine was told to close the window and open it again, which cannot help.</summary>
    [Fact]
    public async Task ABuildWithoutTheEngine_SaysSo_AndWhatHelps_InTheLanguageOfNow()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync(new PowerShellServices(null, null, null));

        Assert.False(window.IsReady);
        Assert.Equal(PageStatusKind.Error, window.Status.Current.Kind);
        Assert.False(window.Status.Current.CanClose);
        Assert.Equal("This installation has no PowerShell engine. Use the standard package of AD Manager, which includes the engine.", window.Status.Current.Message);
        Assert.Equal("This installation has no PowerShell engine.", window.TrustedModeDisabledReason);
        Assert.Contains("PowerShell.Status.NotAvailable", window.Status.Current.Log.ToString(), StringComparison.Ordinal);

        harness.Directory.Localizer.SetCulture(Polish);

        Assert.Equal("Ta instalacja nie zawiera silnika PowerShell. Użyj standardowego pakietu AD Managera, który zawiera silnik.", window.Status.Current.Message);
        Assert.Equal("Ta instalacja nie zawiera silnika PowerShell.", window.TrustedModeDisabledReason);
    }

    /// <summary>
    /// Audit W-10: the switch's tool tip says why it is disabled in every state it is disabled in - before, the session that ended had no
    /// reason and an engine that failed said "not ready yet" for ever. The reason is null exactly when the switch can be pressed.
    /// </summary>
    [Theory]
    [InlineData("failed", "PowerShell could not be started in this window.")]
    [InlineData("no engine", "This installation has no PowerShell engine.")]
    [InlineData("ended", "The session this window was opened in has ended (disconnected or connected again). Close this window and open it again.")]
    [InlineData("disconnected", "Not connected to a domain.")]
    [InlineData("not opened", "PowerShell is not ready yet.")]
    [InlineData("ready", null)]
    public async Task TheSwitch_SaysWhyItIsDisabled_InEveryStateItIsDisabledIn(string state, string? reason)
    {
        var harness = await PowerShellHarness.CreateAsync();
        PowerShellWindowViewModel window;
        switch (state)
        {
            case "failed":
                harness.Factory.Failure = new InvalidOperationException("The engine's files are missing.");
                window = await harness.OpenAsync();
                break;
            case "no engine":
                window = await harness.OpenAsync(new PowerShellServices(null, null, null));
                break;
            case "ended":
                window = await harness.OpenAsync();
                await harness.Directory.Sessions.DisconnectAsync();
                break;
            case "disconnected":
                await harness.Directory.Sessions.DisconnectAsync();
                window = await harness.OpenAsync();
                break;
            case "not opened":
                window = harness.Create();
                break;
            default:
                window = await harness.OpenAsync();
                break;
        }

        using var _ = window;
        Assert.Equal(reason, window.TrustedModeDisabledReason);
        Assert.Equal(reason is null, window.CanToggleTrustedMode);
        Assert.Equal(reason is null, window.ToggleTrustedModeCommand.CanExecute(null));
    }

    [Fact]
    public async Task AWindowOpenedWhileDisconnected_SaysItIsNotConnected_AndStartsNothing()
    {
        var harness = await PowerShellHarness.CreateAsync();
        await harness.Directory.Sessions.DisconnectAsync();
        using var window = await harness.OpenAsync();

        Assert.Equal(PageStatusKind.Error, window.Status.Current.Kind);
        Assert.Empty(harness.Factory.Requests);
        Assert.Equal("PowerShell", window.WindowTitle);
        Assert.False(window.IsReady);
    }

    [Fact]
    public async Task TheEndOfTheSession_StopsTheRunningCommand_AndIsTheStateThatLasts()
    {
        var harness = await PowerShellHarness.CreateAsync(policies: Transcribing);
        using var window = await harness.OpenAsync();
        harness.Session.QueueHeld([Text("partial\n")], Text("more\n"));
        var started = harness.Session.NextRunStarted();
        PowerShellHarness.Type(window, "slow");
        var run = window.RunCommand.ExecuteAsync(null);
        var fake = await started.WaitAsync(Limit, Ct);
        await fake.Reached.WaitAsync(Limit, Ct);

        await harness.Directory.Sessions.DisconnectAsync();
        await run.WaitAsync(Limit, Ct);

        Assert.True(window.IsSessionEnded);
        Assert.True(fake.Token.IsCancellationRequested);
        Assert.False(window.IsRunning);
        Assert.False(window.IsReady);
        window.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Error, window.Status.Current.Kind);
        Assert.False(window.Status.Current.CanClose);
        PowerShellHarness.Type(window, "again");
        Assert.False(window.RunCommand.CanExecute(null));
    }

    // ---- The policies ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task APolicyThatRecords_IsANoteThatLastsBeforeTheFirstCommand_AndStaysInTheScrollback()
    {
        var harness = await PowerShellHarness.CreateAsync(policies: Transcribing);
        using var window = await harness.OpenAsync();

        Assert.Equal(1, harness.Policies.Reads);
        var note = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, note.Kind);
        Assert.StartsWith("Transcription is on (computer policy): the commands you run here and their results", note.Message, StringComparison.Ordinal);
        Assert.Contains(Directory, note.Message, StringComparison.Ordinal);
        Assert.EndsWith("AD Manager does not change these policies and does not get around them.", note.Message, StringComparison.Ordinal);
        Assert.False(note.CanClose);
        Assert.Same(window.Status.Lasting, window.Status.Current);
        Assert.Equal(note.Message, window.PolicyNoteText);
        Assert.Contains(window.Output.Lines, line => line.Kind == PowerShellLineKind.PolicyNote && line.Text == note.Message);

        // The log copy is the key and a mask, never the directory of the transcripts.
        Assert.DoesNotContain("transcripts", note.Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNote_IsThereBeforeAnyCommandCanRun_BecauseThePoliciesAreReadWhileTheEngineStarts()
    {
        var harness = await PowerShellHarness.CreateAsync(policies: Transcribing);
        harness.Policies.Hold();
        using var window = harness.Create();

        var opening = window.InitializeAsync();
        await UntilAsync(window, () => window.IsStarting).WaitAsync(Limit, Ct);
        Assert.False(window.IsReady);
        harness.Policies.Release();
        await opening.WaitAsync(Limit, Ct);

        Assert.True(window.IsReady);
        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);
    }

    [Fact]
    public async Task ACommandCoversTheNote_AndTheNoteIsBackWhenItsResultIsClosed()
    {
        var harness = await PowerShellHarness.CreateAsync(policies: Transcribing);
        using var window = await harness.OpenAsync();
        var note = window.Status.Current.Message;

        await PowerShellHarness.RunAsync(window, "Get-Date");
        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);

        window.Status.CloseCommand.Execute(null);
        Assert.Equal(note, window.Status.Current.Message);
    }

    [Theory]
    [InlineData("script", "Script block logging is on (computer policy)")]
    [InlineData("module", "Module logging is on (computer policy, modules: ActiveDirectory)")]
    public async Task EachKindOfRecording_HasItsOwnNote(string kind, string expectedStart)
    {
        var policies = kind == "script"
            ? new PowerShellPolicies { ScriptBlockLogging = new PowerShellScriptBlockLoggingPolicy(true, false, CoreComputer) }
            : new PowerShellPolicies { ModuleLogging = new PowerShellModuleLoggingPolicy(true, ["ActiveDirectory"], CoreComputer) };
        var harness = await PowerShellHarness.CreateAsync(policies: policies);
        using var window = await harness.OpenAsync();

        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);
        Assert.StartsWith(expectedStart, window.Status.Current.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PowerShellLockdownState.None)]
    [InlineData(PowerShellLockdownState.Audit)]
    [InlineData(PowerShellLockdownState.Enforce)]
    public async Task WhenNothingRecords_ThereIsNoNote_WhateverTheLockdownAndTheExecutionPolicySay(PowerShellLockdownState lockdown)
    {
        var policies = new PowerShellPolicies { Lockdown = lockdown, ExecutionPolicy = new PowerShellExecutionPolicyPolicy("AllSigned", CoreComputer) };
        var harness = await PowerShellHarness.CreateAsync(policies: policies);
        using var window = await harness.OpenAsync();

        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
        Assert.Null(window.Status.Lasting);
        Assert.Null(window.PolicyNoteText);
        Assert.DoesNotContain(window.Output.Lines, line => line.Kind == PowerShellLineKind.PolicyNote);
        Assert.Same(policies, window.Policies);
    }

    [Fact]
    public async Task APolicyThatCannotBeRead_IsSaid_BecauseItIsNotKnownWhetherAnythingIsRecorded()
    {
        var harness = await PowerShellHarness.CreateAsync();
        harness.Policies.Failure = new UnauthorizedAccessException("Access to the registry key is denied.");
        using var window = await harness.OpenAsync();

        Assert.True(window.IsReady);
        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);
        Assert.Equal(
            "The PowerShell policies of this workstation could not be read: Access to the registry key is denied. Whether commands and results are recorded is unknown.",
            window.Status.Current.Message);
    }

    [Fact]
    public async Task AHostWithoutAPolicyReader_ReadsNothing_AndSaysNothing()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync(new PowerShellServices(harness.Factory, null, harness.Actions));

        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
        Assert.True(window.IsReady);
    }

    [Fact]
    public async Task TheNote_FollowsTheLanguage()
    {
        var harness = await PowerShellHarness.CreateAsync(policies: Transcribing);
        using var window = await harness.OpenAsync();

        harness.Directory.Localizer.SetCulture(Polish);

        Assert.StartsWith("Transkrypcja jest włączona (zasady komputera)", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.EndsWith("AD Manager nie zmienia tych zasad i ich nie omija.", window.PolicyNoteText, StringComparison.Ordinal);
    }

    // ---- The language mode ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheWindowStartsConstrained_AndTheTrustedModeNeedsAConfirmation_ThatSaysWhatItAllows()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        Assert.Equal(PowerShellLanguageMode.Constrained, window.LanguageMode);
        Assert.True(window.CanToggleTrustedMode);
        Assert.Null(window.TrustedModeDisabledReason);

        // No: nothing changes.
        harness.Directory.Dialogs.ConfirmAnswers.Enqueue(false);
        await window.ToggleTrustedModeCommand.ExecuteAsync(null);
        Assert.False(window.IsTrustedMode);
        Assert.Empty(harness.Session.ModeRequests);

        var question = Assert.Single(harness.Directory.Dialogs.Shown, shown => shown.StartsWith("confirm:", StringComparison.Ordinal));
        Assert.Contains(harness.Directory.Session.Info.DomainDnsName, question, StringComparison.Ordinal);
        Assert.Contains("any .NET code", question, StringComparison.Ordinal);
        Assert.Contains("around the change plan", question, StringComparison.Ordinal);
        Assert.Contains("not a security boundary", question, StringComparison.Ordinal);
        Assert.Contains("not remembered", question, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTrustedMode_IsInTheTitle_AndInANoteThatLasts_AndIsOffAgainWithoutAQuestion()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Directory.Dialogs.ConfirmAnswers.Enqueue(true);

        await window.ToggleTrustedModeCommand.ExecuteAsync(null);

        Assert.True(window.IsTrustedMode);
        Assert.Equal([PowerShellLanguageMode.Full], harness.Session.ModeRequests);
        Assert.EndsWith("trusted mode", window.WindowTitle, StringComparison.Ordinal);
        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);
        Assert.StartsWith("Trusted mode is on: scripts in this window can run any .NET code", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.False(window.Status.Current.CanClose);
        Assert.Contains((PowerShellLineKind.Information, "Trusted mode is on for this window: FullLanguage, any .NET code. It is not remembered; a new window starts in ConstrainedLanguage."),
            window.Output.Lines.Select(line => (line.Kind, line.Text)));
        window.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);

        // Off: no question.
        var questions = harness.Directory.Dialogs.Shown.Count(shown => shown.StartsWith("confirm:", StringComparison.Ordinal));
        await window.ToggleTrustedModeCommand.ExecuteAsync(null);
        Assert.False(window.IsTrustedMode);
        Assert.Equal(questions, harness.Directory.Dialogs.Shown.Count(shown => shown.StartsWith("confirm:", StringComparison.Ordinal)));
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
        Assert.DoesNotContain("trusted", window.WindowTitle, StringComparison.Ordinal);
        Assert.Contains(" · ConstrainedLanguage", window.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTrustedMode_IsNeverRemembered_ANewWindowStartsConstrained()
    {
        var first = await PowerShellHarness.CreateAsync();
        using var trusted = await first.OpenAsync();
        first.Directory.Dialogs.ConfirmAnswers.Enqueue(true);
        await trusted.ToggleTrustedModeCommand.ExecuteAsync(null);
        Assert.True(trusted.IsTrustedMode);

        var second = await PowerShellHarness.CreateAsync();
        using var fresh = await second.OpenAsync();

        Assert.False(fresh.IsTrustedMode);
        Assert.Equal(PowerShellLanguageMode.Constrained, fresh.LanguageMode);
        Assert.DoesNotContain("trusted", fresh.WindowTitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNotes_OfThePolicyAndOfTheTrustedMode_AreOneNoteTogether_AndEachIsAloneWhenTheOtherGoes()
    {
        var harness = await PowerShellHarness.CreateAsync(policies: Transcribing);
        using var window = await harness.OpenAsync();
        var policyNote = window.PolicyNoteText!;
        harness.Directory.Dialogs.ConfirmAnswers.Enqueue(true);

        await window.ToggleTrustedModeCommand.ExecuteAsync(null);

        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);
        Assert.StartsWith(policyNote, window.Status.Current.Message, StringComparison.Ordinal);
        Assert.Contains("Trusted mode is on", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.Equal(2, Assert.Single(window.Status.Current.Log.Counts));
        Assert.Contains("PowerShell.Status.Notes", window.Status.Current.Log.ToString(), StringComparison.Ordinal);

        await window.ToggleTrustedModeCommand.ExecuteAsync(null);
        Assert.Equal(policyNote, window.Status.Current.Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnderALockdown_TheSwitchIsDisabledWithTheReason_AndTheScrollbackSaysWhy(bool sessionSaysSo, bool policySaysSo)
    {
        var harness = await PowerShellHarness.CreateAsync(policies: new PowerShellPolicies { Lockdown = policySaysSo ? PowerShellLockdownState.Enforce : PowerShellLockdownState.None });
        harness.Session.TrustedModeBlock = sessionSaysSo ? PowerShellTrustedModeBlock.SystemLockdown : PowerShellTrustedModeBlock.None;
        using var window = await harness.OpenAsync();

        Assert.False(window.CanToggleTrustedMode);
        Assert.False(window.ToggleTrustedModeCommand.CanExecute(null));
        Assert.Equal("A system policy (WDAC or AppLocker) enforces ConstrainedLanguage mode.", window.TrustedModeDisabledReason);
        Assert.Contains(window.Output.Lines, line => line.Text.StartsWith("A system policy (WDAC or AppLocker) enforces ConstrainedLanguage mode: the trusted mode is not available", StringComparison.Ordinal));
        Assert.Equal(PowerShellLanguageMode.Constrained, window.LanguageMode);
        Assert.DoesNotContain(harness.Directory.Dialogs.Shown, shown => shown.StartsWith("confirm:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAuditLockdown_DoesNotDisableTheSwitch()
    {
        var harness = await PowerShellHarness.CreateAsync(policies: new PowerShellPolicies { Lockdown = PowerShellLockdownState.Audit });
        using var window = await harness.OpenAsync();

        Assert.True(window.CanToggleTrustedMode);
        Assert.DoesNotContain(window.Output.Lines, line => line.Text.Contains("WDAC", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhileACommandRuns_TheSwitchIsDisabled_WithItsReason()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.QueueHeld([Text("partial\n")], Text("more\n"));
        var started = harness.Session.NextRunStarted();
        PowerShellHarness.Type(window, "slow");
        var run = window.RunCommand.ExecuteAsync(null);
        var fake = await started.WaitAsync(Limit, Ct);
        await fake.Reached.WaitAsync(Limit, Ct);

        Assert.False(window.CanToggleTrustedMode);
        Assert.Equal("Not while a command is running.", window.TrustedModeDisabledReason);

        fake.Release();
        await run.WaitAsync(Limit, Ct);
        Assert.True(window.CanToggleTrustedMode);
        Assert.Null(window.TrustedModeDisabledReason);
    }

    [Fact]
    public async Task AModeThatTheEngineRefuses_IsAnAttention_AndTheModeStaysAsItWas()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.QueueHeld([Text("partial\n")], Text("more\n"));

        // While the question is open a command is started: the engine refuses the switch.
        FakeRun? fake = null;
        harness.Directory.Dialogs.BeforeConfirmAnswer = async () =>
        {
            var started = harness.Session.NextRunStarted();
            PowerShellHarness.Type(window, "slow");
            _ = window.RunCommand.ExecuteAsync(null);
            fake = await started;
            await fake.Reached;
        };
        harness.Directory.Dialogs.ConfirmAnswers.Enqueue(true);

        await window.ToggleTrustedModeCommand.ExecuteAsync(null);

        // The engine refused: the mode stays, the command goes on, and the line is still its - the refusal is in the scrollback, in the
        // window's words (the engine's English message is not shown).
        Assert.False(window.IsTrustedMode);
        Assert.Equal(PageStatusKind.Working, window.Status.Current.Kind);
        Assert.True(window.IsRunning);
        Assert.Contains(window.Output.Lines, line => line.Text == "The language mode was not changed: Not while a command is running.");
        fake!.Release();
        await UntilAsync(window, () => !window.IsRunning).WaitAsync(Limit, Ct);
        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);
    }

    [Fact]
    public async Task ADisposedWindow_CancelsTheCreationOfTheSessionItWasWaitingFor()
    {
        var harness = await PowerShellHarness.CreateAsync();
        harness.Factory.Hold();
        var window = harness.Create();
        var opening = window.InitializeAsync();
        await UntilAsync(window, () => window.IsStarting).WaitAsync(Limit, Ct);

        window.Dispose();
        await opening.WaitAsync(Limit, Ct);

        Assert.False(window.IsReady);
        Assert.False(window.IsStarting);
    }

    [Fact]
    public async Task AModeThatTheEngineRefusesWhileIdle_SaysWhy()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Directory.Dialogs.ConfirmAnswers.Enqueue(true);
        // The lockdown comes into force after the window looked: the engine asks again and refuses.
        harness.Directory.Dialogs.BeforeConfirmAnswer = () =>
        {
            harness.Session.TrustedModeBlock = PowerShellTrustedModeBlock.SystemLockdown;
            return Task.CompletedTask;
        };

        await window.ToggleTrustedModeCommand.ExecuteAsync(null);

        Assert.False(window.IsTrustedMode);
        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);
        Assert.Equal("The language mode was not changed: A system policy (WDAC or AppLocker) enforces ConstrainedLanguage mode.", window.Status.Current.Message);
        Assert.Equal(PowerShellLanguageMode.Constrained, window.LanguageMode);

        harness.Directory.Localizer.SetCulture(Polish);
        Assert.Equal("Zasady systemowe (WDAC lub AppLocker) wymuszają tryb ConstrainedLanguage.", window.TrustedModeDisabledReason);
    }

    // ---- The end ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheWindowKeepsAPlaceForThePendingPlan_EmptyAndHidden()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();

        Assert.False(window.PendingPlan.IsVisible);
        Assert.Equal(0, window.PendingPlan.OperationCount);
        await PowerShellHarness.RunAsync(window, "Get-AdmUser *");
        Assert.False(window.PendingPlan.IsVisible);
    }

    [Fact]
    public async Task DisposingTheWindow_ClosesTheSession_OnceAndSafely()
    {
        var harness = await PowerShellHarness.CreateAsync();
        var window = await harness.OpenAsync();

        window.Dispose();
        window.Dispose();

        await harness.Session.Disposed.WaitAsync(Limit, Ct);
        Assert.Equal(1, harness.Session.Disposals);
        Assert.True(window.IsDisposed);
    }

    [Fact]
    public async Task DisposingBeforeTheEngineIsStarted_ClosesTheSessionAsSoonAsItExists()
    {
        var harness = await PowerShellHarness.CreateAsync();
        harness.Factory.Hold();
        harness.Factory.HonorsCancellation = false;
        var window = harness.Create();
        var opening = window.InitializeAsync();
        await UntilAsync(window, () => window.IsStarting).WaitAsync(Limit, Ct);

        window.Dispose();
        harness.Factory.Release();
        await opening.WaitAsync(Limit, Ct);

        await harness.Session.Disposed.WaitAsync(Limit, Ct);
        Assert.Equal(1, harness.Session.Disposals);
        Assert.False(window.IsReady);
    }

    [Fact]
    public async Task TheWelcome_NamesTheGuideOfItsLanguage_AndTheGuideExists()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        Assert.Contains(window.Output.Lines, line => line.Text.Contains("docs/en/POWERSHELL.md", StringComparison.Ordinal));

        harness.Directory.Localizer.SetCulture(Polish);
        var polish = harness.Directory.Localizer[AdMng.Presentation.Localization.L.PowerShell.WelcomeHelp];
        Assert.Contains("docs/POWERSHELL.md", polish, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/en/", polish, StringComparison.Ordinal);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AdMng.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        Assert.True(File.Exists(Path.Combine(root.FullName, "docs", "POWERSHELL.md")));
        Assert.True(File.Exists(Path.Combine(root.FullName, "docs", "en", "POWERSHELL.md")));
    }

    [Fact]
    public async Task NoLogCopy_OfAnythingTheWindowSays_CarriesAValue()
    {
        var harness = await PowerShellHarness.CreateAsync(policies: Transcribing);
        using var window = await harness.OpenAsync();
        var seen = harness.Record(window);
        harness.Session.Queue(Text("secret-object-name\n", PowerShellStream.Error), End(PowerShellRunState.Completed, hadErrors: true));
        await PowerShellHarness.RunAsync(window, "Get-AdmUser secret-object-name");

        Assert.NotEmpty(seen.Seen);
        Assert.DoesNotContain(harness.Directory.Session.Info.Server, window.Status.Current.Log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-object-name", window.Status.Current.Log.ToString(), StringComparison.Ordinal);
    }
}
