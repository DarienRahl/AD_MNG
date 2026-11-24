using System.Globalization;
using AdMng.Core.PowerShell;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.PowerShell;
using static AdMng.Presentation.Tests.PowerShell.FakePowerShellSession;
using static AdMng.Presentation.Tests.PowerShell.PowerShellWindowTestSupport;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>
/// Running a command in the PowerShell window (track R, stage R1): the echo, the stream of the engine's items, the cap of the
/// scrollback, Stop, the question when the window is closed meanwhile, the questions of a command and the history. The engine is
/// a fake that streams what the test says, from a thread of its own like the real one.
/// </summary>
public sealed class PowerShellWindowRunTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IEnumerable<(PowerShellLineKind Kind, string Text)> Lines(PowerShellWindowViewModel window) =>
        window.Output.Lines.Select(line => (line.Kind, line.Text));

    [Fact]
    public async Task ARun_EchoesTheCommand_StreamsItsText_AndTheLineSaysWhatItDidAndHowLong()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.Queue(_ => TakesTime(harness.Directory.Time, TimeSpan.FromMilliseconds(800), Text("Monday\n"), Text("Tuesday\n")));
        var seen = harness.Record(window);

        await PowerShellHarness.RunAsync(window, "Get-Date");

        Assert.Equal(["Get-Date"], harness.Session.Scripts);
        Assert.Equal(
            [(PowerShellLineKind.Command, "PS> Get-Date"), (PowerShellLineKind.Output, "Monday"), (PowerShellLineKind.Output, "Tuesday")],
            Lines(window).Skip(2));
        Assert.Equal(
            [(PageStatusKind.Working, "Running the command…"), (PageStatusKind.Done, "The command finished: 2 lines of output in the window, 0.8 s.")],
            seen.Plain);
        Assert.False(window.IsRunning);
        Assert.True(window.Input.IsBlank);
        Assert.Equal(["Get-Date"], window.History.Entries);
    }

    [Fact]
    public async Task ThePolishLine_UsesThePolishPluralAndDecimalComma()
    {
        var harness = await PowerShellHarness.CreateAsync(Polish);
        using var window = await harness.OpenAsync();
        harness.Session.Queue(_ => TakesTime(harness.Directory.Time, TimeSpan.FromMilliseconds(800), Text("a\n"), Text("b\n"), Text("c\n"), Text("d\n"), Text("e\n")));
        var seen = harness.Record(window);

        await PowerShellHarness.RunAsync(window, "1..5");

        Assert.Equal("Polecenie zakończyło się: 5 wierszy danych wyjściowych w oknie, 0,8 s.", seen.Plain[^1].Message);
        harness.Session.Queue(Text("x\n"), Text("y\n"));
        await PowerShellHarness.RunAsync(window, "1..2");
        Assert.Equal("Polecenie zakończyło się: 2 wiersze danych wyjściowych w oknie, 0,0 s.", window.Status.Current.Message);
    }

    [Fact]
    public async Task ACommandWithoutOutput_SaysSo()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();

        await PowerShellHarness.RunAsync(window, "$x = 1");

        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);
        Assert.Equal("The command finished with no output, 0.0 s.", window.Status.Current.Message);
    }

    [Fact]
    public async Task AMultiLineCommand_IsEchoedWithTheContinuationPrompt()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();

        await PowerShellHarness.RunAsync(window, "foreach ($u in 1..2) {\n  $u\n}");

        Assert.Equal(
            ["PS> foreach ($u in 1..2) {", ">>   $u", ">> }"],
            window.Output.Lines.Where(line => line.Kind == PowerShellLineKind.Command).Select(line => line.Text));
    }

    [Fact]
    public async Task APartialLine_GrowsUntilItsLineEnd_AndItIsOneLine()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.Queue(new PowerShellTextItem(PowerShellStream.Output, "a", ConsoleColor.Red), Text("b\n"), Text("c"));

        await PowerShellHarness.RunAsync(window, "Write-Host a b");

        var output = window.Output.Lines.Where(line => line.Kind == PowerShellLineKind.Output).ToList();
        Assert.Equal(["ab", "c"], output.Select(line => line.Text));
        Assert.Equal(ConsoleColor.Red, output[0].Runs[0].Foreground);
        Assert.Null(output[0].Runs[1].Foreground);
        Assert.All(output, line => Assert.False(line.IsOpen));
        Assert.Equal("The command finished: 2 lines of output in the window, 0.0 s.", window.Status.Current.Message);
    }

    [Fact]
    public async Task EveryStream_HasItsKind()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.Queue(
            Text("o\n"), Text("e\n", PowerShellStream.Error), Text("w\n", PowerShellStream.Warning), Text("v\n", PowerShellStream.Verbose), Text("d\n", PowerShellStream.Debug));

        await PowerShellHarness.RunAsync(window, "all");

        Assert.Equal(
            [PowerShellLineKind.Output, PowerShellLineKind.Error, PowerShellLineKind.Warning, PowerShellLineKind.Verbose, PowerShellLineKind.Debug],
            window.Output.Lines.Skip(3).Select(line => line.Kind));
    }

    [Fact]
    public async Task ADistinguishedNameInAResult_IsALink_ThatOpensTheObject()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        const string Dn = "CN=Łukasz Kowalski,OU=Sprzedaż,DC=contoso,DC=test";
        harness.Session.Queue(Text($"Name  DistinguishedName\n"), Text($"Łukasz  {Dn}\n"));

        await PowerShellHarness.RunAsync(window, "Get-AdmUser Łukasz");

        var link = window.Output.Lines.SelectMany(line => line.Runs).Single(run => run.IsLink);
        Assert.Equal(Dn, link.DistinguishedName);
        await window.OpenObjectCommand.ExecuteAsync(link.DistinguishedName);
        Assert.Equal([Dn], harness.Directory.Dialogs.PropertiesShown);
    }

    [Fact]
    public async Task AResultOf100000Lines_ShowsTheLastOnes_InStepsOfTheWindowsOwn_AndSaysWhatWasDropped()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.Queue([.. Enumerable.Range(1, 100_000).Select(i => (PowerShellOutputItem)Text($"Line {i}\n"))]);
        var invokesBefore = harness.Directory.Dispatcher.InvokeCount;

        await PowerShellHarness.RunAsync(window, "1..100000");

        Assert.InRange(window.Output.Lines.Count, PowerShellOutputDocument.MaxLines, PowerShellOutputDocument.MaxLines + PowerShellOutputDocument.Slack);
        Assert.Equal("Line 100000", window.Output.Lines[^1].Text);
        Assert.Equal(2 + 1 + 100_000, window.Output.Lines.Count + window.Output.DroppedLines);
        Assert.Equal("The oldest " + window.Output.DroppedLines.ToString("N0", CultureInfo.GetCultureInfo("en-US")) + " lines were removed from this window to keep it fast; it keeps the last 20,000.", window.DroppedNote);
        Assert.Equal("The command finished: 100,000 lines of output in the window, 0.0 s.", window.Status.Current.Message);

        // The window was fed in steps: one hand-over to the UI thread per batch, not one per line.
        Assert.True(harness.Directory.Dispatcher.InvokeCount - invokesBefore < 1_000, "one hand-over per line would be 100,000");
    }

    [Fact]
    public async Task ALineIsShown_AsSoonAsItWasWritten_WhileTheCommandStillRuns()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.QueueHeld([Text("first\n")], Text("second\n"));
        var started = harness.Session.NextRunStarted();
        Type(window, "slow");

        var run = window.RunCommand.ExecuteAsync(null);
        var fake = await started.WaitAsync(Limit, Ct);
        await fake.Reached.WaitAsync(Limit, Ct);

        // The engine has more to say later; what it said is already in the window (the pump hands it over without waiting for the end).
        await UntilAsync(window.Output.Lines, () => window.Output.Lines.Any(line => line.Text == "first")).WaitAsync(Limit, Ct);
        Assert.DoesNotContain("second", window.Output.Lines.Select(line => line.Text));
        Assert.True(window.IsRunning);
        Assert.True(window.Status.IsWorking);
        Assert.False(window.RunCommand.CanExecute(null));

        fake.Release();
        await run.WaitAsync(Limit, Ct);
        Assert.Contains("second", window.Output.Lines.Select(line => line.Text));
        Assert.False(window.IsRunning);

        static void Type(PowerShellWindowViewModel w, string t) => PowerShellHarness.Type(w, t);
    }

    [Fact]
    public async Task Stop_SaysStopping_ThenEndsAsAnAttention_AndTheOutputSoFarStays()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.QueueHeld([Text("partial\n")], Text("never\n"));
        var seen = harness.Record(window);
        var started = harness.Session.NextRunStarted();
        PowerShellHarness.Type(window, "while ($true) { }");

        var run = window.RunCommand.ExecuteAsync(null);
        var fake = await started.WaitAsync(Limit, Ct);
        await fake.Reached.WaitAsync(Limit, Ct);
        harness.Directory.Time.Advance(TimeSpan.FromMilliseconds(1_500));
        window.StopCommand.Execute(null);
        await run.WaitAsync(Limit, Ct);

        Assert.Equal(
            [
                (PageStatusKind.Working, "Running the command…"),
                (PageStatusKind.Working, "Stopping…"),
                (PageStatusKind.Attention, "The command was stopped after 1.5 s. The output until then stays in the window."),
            ],
            seen.Plain);
        Assert.Contains("partial", window.Output.Lines.Select(line => line.Text));
        Assert.DoesNotContain("never", window.Output.Lines.Select(line => line.Text));
        Assert.False(window.IsRunning);
    }

    /// <summary>
    /// The engine left a command that did not end after Stop (stuck in .NET code, second red team of R1) and goes on in a new session:
    /// the line says so as an attention, the scrollback says what is gone, and the window runs the next command.
    /// </summary>
    [Fact]
    public async Task ACommandTheEngineLeftAfterStop_IsAnAttention_TheWindowSaysItGoesOnInANewSession_AndRunsTheNextCommand()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.Queue(LeftAfterStop);
        var seen = harness.Record(window);
        var started = harness.Session.NextRunStarted();
        PowerShellHarness.Type(window, "[regex]::new('^(a+)+$', 'None', [regex]::InfiniteMatchTimeout).IsMatch('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!')");

        var run = window.RunCommand.ExecuteAsync(null);
        var fake = await started.WaitAsync(Limit, Ct);
        await fake.Reached.WaitAsync(Limit, Ct);
        harness.Directory.Time.Advance(TimeSpan.FromMilliseconds(6_500));
        window.StopCommand.Execute(null);
        await run.WaitAsync(Limit, Ct);

        Assert.Equal(
            [
                (PageStatusKind.Working, "Running the command…"),
                (PageStatusKind.Working, "Stopping…"),
                (PageStatusKind.Attention, "The command was stopped after 6.5 s but did not end. The window left it and goes on in a new session."),
            ],
            seen.Plain);
        Assert.Contains(
            (PowerShellLineKind.Information, "The command did not end after it was stopped: it is in .NET code that Stop cannot interrupt. The window left it and started a new PowerShell session, so the variables, functions and modules of earlier commands are gone. The command may keep a processor core busy until it ends or the program closes."),
            Lines(window));
        Assert.Contains("partial", window.Output.Lines.Select(line => line.Text));
        Assert.False(window.IsRunning);
        Assert.False(window.IsTrustedMode);

        harness.Session.Queue(Text("next\n"));
        await PowerShellHarness.RunAsync(window, "'next'");
        Assert.Equal("next", window.Output.Lines[^1].Text);
        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);
    }

    [Fact]
    public async Task ACommandTheEngineLeft_InPolish_SaysSoInPolish()
    {
        var harness = await PowerShellHarness.CreateAsync(Polish);
        using var window = await harness.OpenAsync();
        harness.Session.Queue(LeftAfterStop);
        var started = harness.Session.NextRunStarted();
        PowerShellHarness.Type(window, "Wait-Forever");

        var run = window.RunCommand.ExecuteAsync(null);
        await (await started.WaitAsync(Limit, Ct)).Reached.WaitAsync(Limit, Ct);
        harness.Directory.Time.Advance(TimeSpan.FromMilliseconds(6_500));
        window.StopCommand.Execute(null);
        await run.WaitAsync(Limit, Ct);

        Assert.Equal("Polecenie zatrzymano po 6,5 s, ale się nie zakończyło. Okno je zostawiło i działa dalej w nowej sesji.", window.Status.Current.Message);
        Assert.Contains(window.Output.Lines, line => line.Kind == PowerShellLineKind.Information && line.Text.StartsWith("Polecenie nie zakończyło się po zatrzymaniu", StringComparison.Ordinal));
    }

    /// <summary>A lockdown that came into force meanwhile: the engine's new session is constrained, and the window's mode follows it.</summary>
    [Fact]
    public async Task ACommandTheEngineLeftInTheTrustedMode_UnderANewLockdown_EndsTheTrustedModeOfTheWindow()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Directory.Dialogs.ConfirmAnswers.Enqueue(true);
        await window.ToggleTrustedModeCommand.ExecuteAsync(null);
        Assert.True(window.IsTrustedMode);
        harness.Session.Queue(fakeRun => LeftAfterStop(fakeRun, harness.Session.EnforceConstrainedFromTheEngine));
        var started = harness.Session.NextRunStarted();
        PowerShellHarness.Type(window, "Wait-Forever");
        var title = new List<string>();
        window.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PowerShellWindowViewModel.WindowTitle))
            {
                title.Add(window.WindowTitle);
            }
        };

        var run = window.RunCommand.ExecuteAsync(null);
        await (await started.WaitAsync(Limit, Ct)).Reached.WaitAsync(Limit, Ct);
        window.StopCommand.Execute(null);
        await run.WaitAsync(Limit, Ct);

        Assert.False(window.IsTrustedMode);
        Assert.Equal(
            (PowerShellLineKind.Information, "Trusted mode is off: ConstrainedLanguage, in a new PowerShell session. The variables, functions and aliases of earlier commands are gone; the history and the output stay."),
            Lines(window).Last());
        Assert.NotEmpty(title);
        Assert.DoesNotContain("trusted mode", window.WindowTitle, StringComparison.Ordinal);
        Assert.False(window.CanToggleTrustedMode);
    }

    /// <summary>What the engine streams for a run it left: the output so far, then - after Stop - the end of a left run.</summary>
    private static IAsyncEnumerable<PowerShellOutputItem> LeftAfterStop(FakeRun run) => LeftAfterStop(run, () => { });

    private static async IAsyncEnumerable<PowerShellOutputItem> LeftAfterStop(FakeRun run, Action newSession)
    {
        yield return Text("partial\n");
        await run.WaitAsync().ConfigureAwait(false);
        newSession();
        yield return End(PowerShellRunState.Abandoned);
    }

    [Fact]
    public async Task Stop_WithNothingRunning_DoesNothing()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        var seen = harness.Record(window);

        window.StopCommand.Execute(null);

        Assert.Empty(seen.Seen);
    }

    [Fact]
    public async Task ClosingTheWindowWhileACommandRuns_Asks_NoKeepsItRunning_YesStopsItAndClosesAfterItsEnd()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.QueueHeld([Text("partial\n")], Text("more\n"));
        var started = harness.Session.NextRunStarted();
        PowerShellHarness.Type(window, "slow");
        var run = window.RunCommand.ExecuteAsync(null);
        var fake = await started.WaitAsync(Limit, Ct);
        await fake.Reached.WaitAsync(Limit, Ct);

        // No: the window stays and the command goes on.
        harness.Directory.Dialogs.ConfirmAnswers.Enqueue(false);
        Assert.False(await window.CanCloseAsync());
        Assert.True(window.IsRunning);
        Assert.False(window.Status.IsCancelling);
        var question = Assert.Single(harness.Directory.Dialogs.Shown, shown => shown.StartsWith("confirm:", StringComparison.Ordinal));
        Assert.Contains("Running the command…", question, StringComparison.Ordinal);
        Assert.Contains("Stop the running operation and close the window?", question, StringComparison.Ordinal);

        // Yes: it is stopped and the window may close once it ended.
        harness.Directory.Dialogs.ConfirmAnswers.Enqueue(true);
        Assert.True(await window.CanCloseAsync().WaitAsync(Limit, Ct));
        await run.WaitAsync(Limit, Ct);
        Assert.False(window.IsRunning);
    }

    [Fact]
    public async Task ClosingAnIdleWindow_AsksNothing()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        await PowerShellHarness.RunAsync(window, "1");

        Assert.True(await window.CanCloseAsync());

        Assert.DoesNotContain(harness.Directory.Dialogs.Shown, shown => shown.StartsWith("confirm:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposingTheWindowWhileACommandRuns_StopsIt_AndClosesTheSession()
    {
        var harness = await PowerShellHarness.CreateAsync();
        var window = await harness.OpenAsync();
        harness.Session.QueueHeld([Text("partial\n")], Text("more\n"));
        var started = harness.Session.NextRunStarted();
        PowerShellHarness.Type(window, "slow");
        var run = window.RunCommand.ExecuteAsync(null);
        var fake = await started.WaitAsync(Limit, Ct);
        await fake.Reached.WaitAsync(Limit, Ct);

        window.Dispose();

        await run.WaitAsync(Limit, Ct);
        Assert.True(fake.Token.IsCancellationRequested);
        await harness.Session.Disposed.WaitAsync(Limit, Ct);
        Assert.Equal(1, harness.Session.Disposals);
    }

    [Fact]
    public async Task ACommandThatEndsWithErrors_IsInTheWindowAndAnAttentionOnTheLine_WithTheFirstErrorLine()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.Queue(
            Text("Jan\n"),
            Text("Get-AdmUser: Cannot find a user with identity 'nobody': it does not exist, or this account cannot see it.\nLine |\n   1 | Get-AdmUser nobody\n", PowerShellStream.Error),
            End(PowerShellRunState.Completed, hadErrors: true));
        var seen = harness.Record(window);

        await PowerShellHarness.RunAsync(window, "Get-AdmUser nobody");

        Assert.Equal(
            "The command ended with errors: Get-AdmUser: Cannot find a user with identity 'nobody': it does not exist, or this account cannot see it. The error text is in the window.",
            seen.Plain[^1].Message);
        Assert.Equal(PageStatusKind.Attention, seen.Plain[^1].Kind);
        Assert.Equal(3, window.Output.Lines.Count(line => line.Kind == PowerShellLineKind.Error));

        // The log copy is the message key and counts - never the error text, which names the identity.
        var log = window.Status.Current.Log.ToString();
        Assert.DoesNotContain("nobody", log, StringComparison.Ordinal);
        Assert.Contains("PowerShell.Status.WithErrors", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandThatFails_IsAnErrorOnTheLine_WithWhatToDoNext_AndTheTextInTheWindow()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.Queue(Text("ParserError: Missing closing '}' in statement block.\n", PowerShellStream.Error), End(PowerShellRunState.Failed, hadErrors: true));

        await PowerShellHarness.RunAsync(window, "if ($true) {");

        Assert.Equal(PageStatusKind.Error, window.Status.Current.Kind);
        Assert.Equal(
            "The command failed: ParserError: Missing closing '}' in statement block. Correct the command and run it again. The error text is in the window.",
            window.Status.Current.Message);
        Assert.Contains(window.Output.Lines, line => line.Kind == PowerShellLineKind.Error && line.Text.StartsWith("ParserError", StringComparison.Ordinal));
        Assert.True(window.Status.Current.CanClose);
    }

    [Fact]
    public async Task ACommandThatFails_WithoutAnErrorText_StillSaysItFailed()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.Queue(End(PowerShellRunState.Failed, hadErrors: true));

        await PowerShellHarness.RunAsync(window, "boom");

        Assert.Equal("The command failed. Correct the command and run it again. The error text is in the window.", window.Status.Current.Message);
    }

    [Fact]
    public async Task AnEngineThatThrows_IsAnErrorOnTheLine_AndTheWindowCanRunAgain()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.Queue(_ => throw new InvalidOperationException("A run is already active."));

        await PowerShellHarness.RunAsync(window, "x");

        Assert.Equal(PageStatusKind.Error, window.Status.Current.Kind);
        // The reason goes through the program's error translator (audit W-10), not the exception's text alone.
        Assert.StartsWith("The command could not be started: Unexpected error: A run is already active.", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.False(window.IsRunning);

        harness.Session.Queue(Text("ok\n"));
        await PowerShellHarness.RunAsync(window, "y");
        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);
    }

    [Fact]
    public async Task Progress_IsOnTheLine_WithThePartDone_AndTheLineGoesBackToRunningWhenItIsCompleted()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.QueueHeld(
            [new PowerShellProgressItem(1, "Reading", "40 of 100", 40, Completed: false)],
            new PowerShellProgressItem(1, "Reading", "100 of 100", 100, Completed: true));
        var seen = harness.Record(window);
        var started = harness.Session.NextRunStarted();
        PowerShellHarness.Type(window, "long");

        var run = window.RunCommand.ExecuteAsync(null);
        var fake = await started.WaitAsync(Limit, Ct);
        await fake.Reached.WaitAsync(Limit, Ct);
        await UntilAsync(window.Status, () => window.Status.Current.Message == "Reading: 40 of 100").WaitAsync(Limit, Ct);
        Assert.Equal(0.4, window.Status.Current.Progress!.Value.Fraction);
        fake.Release();
        await run.WaitAsync(Limit, Ct);

        Assert.Contains((PageStatusKind.Working, "Running the command…"), seen.Plain);
        Assert.Equal(PageStatusKind.Done, seen.Plain[^1].Kind);
    }

    // ---- The questions of a command --------------------------------------------------------------------------------

    [Fact]
    public async Task ATextQuestion_IsAnsweredFromTheEditor_NotRunAsACommand_AndNotRemembered()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        var prompt = new PowerShellPromptItem(PowerShellPromptKind.Text, null, null, "Name: ");
        object? answer = null;
        harness.Session.Queue(run => Ask(run, prompt, a => answer = a, a => $"Hello {a}"));
        PowerShellHarness.Type(window, "Read-Host 'Name'");
        var run = window.RunCommand.ExecuteAsync(null);

        await UntilAsync(window, () => window.PendingPrompt is not null).WaitAsync(Limit, Ct);
        Assert.Equal("Name: ", window.InputPrompt);
        Assert.Equal(PowerShellInputMode.Answer, window.Input.Mode);
        Assert.Equal("The command is waiting for your answer…", window.Status.Current.Message);
        PowerShellHarness.Type(window, "Jan");
        window.SubmitCommand.Execute(null);
        await run.WaitAsync(Limit, Ct);

        Assert.Equal("Jan", answer);
        Assert.Null(window.PendingPrompt);
        Assert.Equal("PS> ", window.InputPrompt);
        Assert.Equal(PowerShellInputMode.Command, window.Input.Mode);
        Assert.Contains((PowerShellLineKind.Prompt, "Name: Jan"), Lines(window));
        Assert.Contains((PowerShellLineKind.Output, "Hello Jan"), Lines(window));
        Assert.Equal(["Read-Host 'Name'"], harness.Session.Scripts);
        Assert.Equal(["Read-Host 'Name'"], window.History.Entries);
    }

    [Fact]
    public async Task ASecretQuestion_GetsASecureString_AndTheSecretIsNowhereInTheWindow()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        var prompt = new PowerShellPromptItem(PowerShellPromptKind.Secret, null, null, "Password: ");
        object? answer = null;
        harness.Session.Queue(run => Ask(run, prompt, a => answer = a, _ => "got it"));
        PowerShellHarness.Type(window, "Read-Host -AsSecureString");
        var run = window.RunCommand.ExecuteAsync(null);

        await UntilAsync(window, () => window.PendingPrompt is not null).WaitAsync(Limit, Ct);
        Assert.True(window.Input.IsSecret);
        PowerShellHarness.Type(window, "S3cret!");
        Assert.Empty(window.Input.Spans);
        window.SubmitCommand.Execute(null);
        await run.WaitAsync(Limit, Ct);

        var secure = Assert.IsType<System.Security.SecureString>(answer);
        Assert.Equal(7, secure.Length);
        Assert.DoesNotContain(window.Output.Lines, line => line.Text.Contains("S3cret", StringComparison.Ordinal));
        Assert.DoesNotContain(window.History.Entries, entry => entry.Contains("S3cret", StringComparison.Ordinal));
        Assert.DoesNotContain("S3cret", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.True(window.Input.IsBlank);
        Assert.Equal(PowerShellInputMode.Command, window.Input.Mode);
    }

    [Fact]
    public async Task AChoiceQuestion_TakesALetter_TheDefaultForNothing_HelpForAQuestionMark_AndAsksAgainForNonsense()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        var prompt = new PowerShellPromptItem(
            PowerShellPromptKind.Choice,
            "Confirm",
            "Are you sure you want to perform this action?",
            null,
            [new PowerShellChoice("&Yes", "Continue"), new PowerShellChoice("&No", "Stop")],
            defaultChoice: 0);
        object? answer = null;
        harness.Session.Queue(run => Ask(run, prompt, a => answer = a, a => $"choice {a}"));
        PowerShellHarness.Type(window, "Remove-Thing -Confirm");
        var run = window.RunCommand.ExecuteAsync(null);
        await UntilAsync(window, () => window.PendingPrompt is not null).WaitAsync(Limit, Ct);

        Assert.Contains((PowerShellLineKind.Prompt, "Confirm"), Lines(window));
        Assert.Contains((PowerShellLineKind.Prompt, "Are you sure you want to perform this action?"), Lines(window));
        Assert.Contains((PowerShellLineKind.Prompt, "[Y] Yes  [N] No  [?] Help (default is \"Y\")"), Lines(window));

        PowerShellHarness.Type(window, "x");
        window.SubmitCommand.Execute(null);
        Assert.Contains((PowerShellLineKind.Prompt, "Enter one of the letters shown, or ? for help."), Lines(window));
        Assert.NotNull(window.PendingPrompt);

        PowerShellHarness.Type(window, "?");
        window.SubmitCommand.Execute(null);
        Assert.Contains((PowerShellLineKind.Prompt, "Y - Continue"), Lines(window));
        Assert.NotNull(window.PendingPrompt);

        PowerShellHarness.Type(window, "n");
        window.SubmitCommand.Execute(null);
        await run.WaitAsync(Limit, Ct);

        Assert.Equal(1, answer);
        Assert.Contains((PowerShellLineKind.Prompt, "> No"), Lines(window));
    }

    [Fact]
    public async Task AChoiceQuestion_TakesTheDefault_ForAnEmptyAnswer()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        var prompt = new PowerShellPromptItem(PowerShellPromptKind.Choice, null, null, null, [new PowerShellChoice("&Yes", ""), new PowerShellChoice("&No", "")], defaultChoice: 1);
        object? answer = null;
        harness.Session.Queue(run => Ask(run, prompt, a => answer = a, a => $"choice {a}"));
        PowerShellHarness.Type(window, "x");
        var run = window.RunCommand.ExecuteAsync(null);
        await UntilAsync(window, () => window.PendingPrompt is not null).WaitAsync(Limit, Ct);

        window.SubmitCommand.Execute(null);
        await run.WaitAsync(Limit, Ct);

        Assert.Equal(1, answer);
    }

    [Fact]
    public async Task StopWhileAQuestionIsPending_CancelsIt_AndEndsTheRun()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        var prompt = new PowerShellPromptItem(PowerShellPromptKind.Text, null, null, "Name: ");
        object? answer = "unset";
        harness.Session.Queue(run => Ask(run, prompt, a => answer = a, _ => "x"));
        PowerShellHarness.Type(window, "Read-Host");
        var run = window.RunCommand.ExecuteAsync(null);
        await UntilAsync(window, () => window.PendingPrompt is not null).WaitAsync(Limit, Ct);

        window.StopCommand.Execute(null);
        await run.WaitAsync(Limit, Ct);

        Assert.Null(answer);
        Assert.True(prompt.IsAnswered);
        Assert.Null(window.PendingPrompt);
        Assert.False(window.IsRunning);
    }

    // ---- History ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheHistory_RemembersWhatWasRun_NotAPlainTextPassword_NotARepeat_AndClearKeepsIt()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();

        await PowerShellHarness.RunAsync(window, "Get-AdmUser jan");
        await PowerShellHarness.RunAsync(window, "Get-AdmUser jan");
        await PowerShellHarness.RunAsync(window, "$p = ConvertTo-SecureString 'Sup3r' -AsPlainText -Force");
        await PowerShellHarness.RunAsync(window, "Get-AdmGroup *");

        Assert.Equal(["Get-AdmUser jan", "Get-AdmGroup *"], window.History.Entries);
        // The line that was not remembered was run all the same.
        Assert.Equal(4, harness.Session.Scripts.Count);

        window.ClearCommand.Execute(null);
        Assert.Empty(window.Output.Lines);
        Assert.Equal(["Get-AdmUser jan", "Get-AdmGroup *"], window.History.Entries);
        Assert.True(window.Input.MoveUp());
        Assert.Equal("Get-AdmGroup *", window.Input.Text);
    }

    [Fact]
    public async Task EnterRunsACompleteLine_AndInsertsALineForAnIncompleteOne()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        PowerShellHarness.Type(window, "foreach ($u in 1..2) {");

        window.SubmitCommand.Execute(null);
        Assert.Empty(harness.Session.Scripts);
        Assert.Equal("foreach ($u in 1..2) {\n", window.Input.Text);

        PowerShellHarness.Type(window, "foreach ($u in 1..2) {\n$u\n}");
        window.SubmitCommand.Execute(null);
        await window.RunCommand.ExecutionTask!.WaitAsync(Limit, Ct);

        Assert.Equal(["foreach ($u in 1..2) {\n$u\n}"], harness.Session.Scripts);
    }

    [Fact]
    public async Task ABlankLine_RunsNothing()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        PowerShellHarness.Type(window, "   ");

        Assert.False(window.RunCommand.CanExecute(null));
        window.SubmitCommand.Execute(null);

        Assert.Empty(harness.Session.Scripts);
        Assert.Empty(window.History.Entries);
    }

    [Fact]
    public async Task TheWidthOfTheWindow_IsTheWidthOfTheEnginesFormatting_OfTheNextCommand()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();

        window.SetOutputWidth(10_000);
        Assert.Equal(PowerShellWindowViewModel.MaxOutputWidth, window.OutputWidth);
        window.SetOutputWidth(5);
        Assert.Equal(PowerShellWindowViewModel.MinOutputWidth, window.OutputWidth);
        window.SetOutputWidth(160);
        await PowerShellHarness.RunAsync(window, "Get-AdmUser *");

        Assert.Equal(160, harness.Session.OutputWidth);
        Assert.Equal(120, harness.Factory.Requests[0].OutputWidth);
    }
}
