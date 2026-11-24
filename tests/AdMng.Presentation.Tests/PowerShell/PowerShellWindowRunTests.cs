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

    /// <summary>Audit W-06: a name that ends a line as wide as the engine's formatting may be cut there; the window knows the width it ran with.</summary>
    [Fact]
    public async Task ANameThatEndsALineAsWideAsTheFormatting_IsNoLink_ButTheSameNameInAShorterLineIs()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        const string Dn = "CN=Łukasz Kowalski,OU=Sprzedaż,DC=contoso,DC=te";
        window.SetOutputWidth(60);
        var wrapped = new string('x', 59 - Dn.Length - 1) + " " + Dn;
        harness.Session.Queue(Text(wrapped + "\n"), Text("st\n"), Text("short " + Dn + "\n"));

        await PowerShellHarness.RunAsync(window, "Get-AdmUser Łukasz | Format-List");

        Assert.Equal(59, wrapped.Length);
        var links = window.Output.Lines.SelectMany(line => line.Runs).Where(run => run.IsLink).ToList();
        var link = Assert.Single(links);
        Assert.Equal(Dn, link.DistinguishedName);
        Assert.StartsWith("short ", window.Output.Lines.Single(line => line.Runs.Any(run => run.IsLink)).Text, StringComparison.Ordinal);
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
    public async Task TheResults_GoToTheUiThreadBelowItsInput_SoAFloodLeavesTheKeyboardAndStopFree()
    {
        // Audit W-03: at the dispatcher's normal priority a command that writes without a pause kept an operation of the window
        // queued above the keyboard and the mouse at all times, so Stop could not be pressed.
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.Queue([.. Enumerable.Range(1, 3_000).Select(i => (PowerShellOutputItem)Text($"Line {i}\n"))]);
        var dispatcher = harness.Directory.Dispatcher;
        var invokesBefore = dispatcher.InvokeCount;
        var backgroundBefore = dispatcher.BackgroundInvokeCount;

        await PowerShellHarness.RunAsync(window, "1..3000");

        Assert.Equal("Line 3000", window.Output.Lines[^1].Text);
        var handOvers = dispatcher.BackgroundInvokeCount - backgroundBefore;
        Assert.True(handOvers >= 2, $"the stream was handed over {handOvers} times below input");

        // What else went to the UI thread at the normal priority is the window's own (the end of the run), not the stream.
        Assert.InRange(dispatcher.InvokeCount - invokesBefore - handOvers, 0, 2);
    }

    [Fact]
    public async Task ACommandThatWritesALineEveryMoment_IsHandedOverAtMostOncePerInterval_NotOncePerLine()
    {
        // Audit W-03: a partial step went at once whenever the next line was not there yet, so a command that writes a line every
        // millisecond made one hand-over (and one layout of the window) per line.
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.Queue(_ => Trickle(300));
        var dispatcher = harness.Directory.Dispatcher;
        var before = dispatcher.InvokeCount;
        var backgroundBefore = dispatcher.BackgroundInvokeCount;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await PowerShellHarness.RunAsync(window, "trickle");

        // The first step goes at once, every other one at least an interval after the one before, the last with the end of the run.
        var most = (int)(clock.Elapsed / PowerShellWindowViewModel.HandOverInterval) + 2;
        var handOvers = dispatcher.BackgroundInvokeCount - backgroundBefore;
        Assert.Equal("Line 300", window.Output.Lines[^1].Text);
        Assert.InRange(handOvers, 1, most);
        Assert.True(dispatcher.InvokeCount - before <= most + 2, $"{dispatcher.InvokeCount - before} hand-overs in {clock.ElapsedMilliseconds} ms; at most {most} expected");

        static async IAsyncEnumerable<PowerShellOutputItem> Trickle(int count)
        {
            for (var i = 1; i <= count; i++)
            {
                await Task.Delay(1).ConfigureAwait(false);
                yield return Text($"Line {i}\n");
            }

            yield return FakePowerShellSession.End();
        }
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

    /// <summary>Audit PS-12: <c>Clear-Host</c> / <c>cls</c> did nothing in the window; now it clears it like Ctrl+L, in the order of the output.</summary>
    [Fact]
    public async Task ClearHost_ClearsTheScrollback_InTheOrderOfTheOutput_AndTheLineCountsWhatIsLeft()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.Queue(Text("before\n"), Text("more\n"), PowerShellClearItem.Instance, Text("after\n"));

        await PowerShellHarness.RunAsync(window, "'before'; 'more'; cls; 'after'");

        Assert.Equal([(PowerShellLineKind.Output, "after")], Lines(window));
        Assert.Equal(["'before'; 'more'; cls; 'after'"], window.History.Entries);
        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);
        Assert.StartsWith("The command finished: 1 line of output in the window", window.Status.Current.Message, StringComparison.Ordinal);
    }

    /// <summary>Audit PS-12: <c>exit</c> ended the run without a word; now the window says that the command ended there and the window stays open.</summary>
    [Theory]
    [InlineData("en-US", "The command ended with exit (exit code 5). The window stays open; close it with the window's close button or Alt+F4.")]
    [InlineData("pl-PL", "Polecenie zakończyło się instrukcją exit (kod wyjścia 5). Okno pozostaje otwarte; zamknij je przyciskiem zamykania okna lub klawiszami Alt+F4.")]
    public async Task Exit_EndsTheCommand_AndTheWindowSaysItStaysOpen(string culture, string expected)
    {
        var harness = await PowerShellHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var window = await harness.OpenAsync();
        harness.Session.Queue(Text("before\n"), new PowerShellRunEndItem(PowerShellRunState.Completed, HadErrors: false, ExitCode: 5));

        await PowerShellHarness.RunAsync(window, "'before'; exit 5");

        Assert.Equal((PowerShellLineKind.Information, expected), Lines(window).Last());
        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);
        Assert.False(window.IsRunning);
        Assert.True(window.IsReady);
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

    /// <summary>
    /// Audit PS-07: the window made up a "finished" (or "stopped") end for an engine that ended a run without its end item, which
    /// hid the engine losing it. Such an end is an error of the window now, and the next command runs.
    /// </summary>
    [Fact]
    public async Task AnEngineThatEndsARunWithoutItsEndItem_IsAnErrorOnTheLine_NotAFinishedCommand()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        harness.Session.Queue(_ => Without(Text("x\n")));

        await PowerShellHarness.RunAsync(window, "x");

        Assert.Equal(PageStatusKind.Error, window.Status.Current.Kind);
        Assert.StartsWith("The command could not be started: Unexpected error: ", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.False(window.IsRunning);

        harness.Session.Queue(Text("ok\n"));
        await PowerShellHarness.RunAsync(window, "y");
        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);

        static async IAsyncEnumerable<PowerShellOutputItem> Without(PowerShellOutputItem item)
        {
            yield return item;
            await Task.CompletedTask.ConfigureAwait(false);
        }
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
    public async Task TheEmptyEditor_SaysWhatToType_TheCommandOrTheAnswer_InTheLanguageOfTheWindow()
    {
        // Audit W-07: while a command waited for an answer, the empty editor still said "Type a command. Tab completes…".
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        var changed = new List<string?>();
        window.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var prompt = new PowerShellPromptItem(PowerShellPromptKind.Text, null, null, "Name: ");
        harness.Session.Queue(run => Ask(run, prompt, _ => { }, a => $"Hello {a}"));
        Assert.Equal("Type a command. Tab completes, Enter runs, Shift+Enter starts a new line.", window.EditorHint);

        PowerShellHarness.Type(window, "Read-Host 'Name'");
        var run = window.RunCommand.ExecuteAsync(null);
        await UntilAsync(window, () => window.PendingPrompt is not null).WaitAsync(Limit, Ct);

        Assert.Equal("Type the answer and press Enter.", window.EditorHint);
        Assert.Contains(nameof(PowerShellWindowViewModel.EditorHint), changed);
        harness.Directory.Localizer.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));
        Assert.Equal("Wpisz odpowiedź i naciśnij Enter.", window.EditorHint);

        PowerShellHarness.Type(window, "Jan");
        window.SubmitCommand.Execute(null);
        await run.WaitAsync(Limit, Ct);
        Assert.Equal("Wpisz polecenie. Tab uzupełnia, Enter uruchamia, Shift+Enter zaczyna nowy wiersz.", window.EditorHint);
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

        // Audit W-08: the secret never becomes text of the editor's model (it was a string per key); Enter in the password box hands
        // over a SecureString, and Submit alone answers nothing.
        PowerShellHarness.Type(window, "S3cret!");
        Assert.Equal(string.Empty, window.Input.Text);
        Assert.Empty(window.Input.Spans);
        window.SubmitCommand.Execute(null);
        Assert.NotNull(window.PendingPrompt);
        Assert.False(prompt.IsAnswered);
        using var typed = new System.Security.SecureString();
        foreach (var character in "S3cret!")
        {
            typed.AppendChar(character);
        }

        window.AnswerSecretCommand.Execute(typed.Copy());
        await run.WaitAsync(Limit, Ct);

        var secure = Assert.IsType<System.Security.SecureString>(answer);
        Assert.Equal(7, secure.Length);
        Assert.True(secure.IsReadOnly());
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

    /// <summary>
    /// Audit W-09: a question that arrives while the operator types the next command took the typed text away, and the Enter meant for
    /// that command answered the question with its default (Yes of a -Confirm). The text comes back after the question, and an empty
    /// answer right after such a question asks once more.
    /// </summary>
    [Fact]
    public async Task AQuestionThatComesWhileTheNextCommandIsTyped_KeepsTheText_AndItsFirstEmptyEnterAsksAgain()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new PowerShellPromptItem(PowerShellPromptKind.Choice, "Confirm", "Remove?", null, [new PowerShellChoice("&Yes", ""), new PowerShellChoice("&No", "")], defaultChoice: 0);
        object? answer = null;
        harness.Session.Queue(run => AskAfter(gate.Task, run, prompt, a => answer = a));
        PowerShellHarness.Type(window, "Remove-Item x -Confirm");
        var running = window.RunCommand.ExecuteAsync(null);
        await UntilAsync(window, () => window.IsRunning).WaitAsync(Limit, Ct);

        PowerShellHarness.Type(window, "Get-AdmUser jan.kowalski");
        gate.SetResult();
        await UntilAsync(window, () => window.PendingPrompt is not null).WaitAsync(Limit, Ct);
        Assert.Equal(string.Empty, window.Input.Text);
        Assert.Equal("Get-AdmUser jan.kowalski", window.Input.CommandDraft);

        window.SubmitCommand.Execute(null);
        Assert.False(prompt.IsAnswered);
        Assert.Contains(
            "The question came while you were typing the next command; what you typed comes back after the answer. Type the answer, or press Enter again for the default.",
            window.Output.Lines.Select(line => line.Text));

        window.SubmitCommand.Execute(null);
        await running.WaitAsync(Limit, Ct);
        Assert.Equal(0, answer);
        Assert.Equal("Get-AdmUser jan.kowalski", window.Input.Text);
        Assert.Equal(PowerShellInputMode.Command, window.Input.Mode);
    }

    [Fact]
    public async Task AQuestionWithoutTypeAhead_TakesTheDefaultAtTheFirstEmptyEnter_AndAnAnswerTypedAfterTheWipeCounts()
    {
        var harness = await PowerShellHarness.CreateAsync();
        using var window = await harness.OpenAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new PowerShellPromptItem(PowerShellPromptKind.Choice, null, null, null, [new PowerShellChoice("&Yes", ""), new PowerShellChoice("&No", "")], defaultChoice: 0);
        object? answer = null;
        harness.Session.Queue(run => AskAfter(gate.Task, run, prompt, a => answer = a));
        PowerShellHarness.Type(window, "x");
        var running = window.RunCommand.ExecuteAsync(null);
        await UntilAsync(window, () => window.IsRunning).WaitAsync(Limit, Ct);
        PowerShellHarness.Type(window, "typed ahead");
        gate.SetResult();
        await UntilAsync(window, () => window.PendingPrompt is not null).WaitAsync(Limit, Ct);

        PowerShellHarness.Type(window, "n");
        window.SubmitCommand.Execute(null);
        await running.WaitAsync(Limit, Ct);

        Assert.Equal(1, answer);
        Assert.Equal("typed ahead", window.Input.Text);
    }

    private static async IAsyncEnumerable<PowerShellOutputItem> AskAfter(Task gate, FakeRun run, PowerShellPromptItem prompt, Action<object?> answered)
    {
        await gate.ConfigureAwait(false);
        await foreach (var item in Ask(run, prompt, answered, a => $"choice {a}").ConfigureAwait(false))
        {
            yield return item;
        }
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
        // The line that was not remembered was run all the same, as typed; the scrollback echoes it with its secret masked (audit PS-13).
        Assert.Equal(4, harness.Session.Scripts.Count);
        Assert.Equal("$p = ConvertTo-SecureString 'Sup3r' -AsPlainText -Force", harness.Session.Scripts[2]);
        Assert.Contains("PS> $p = ConvertTo-SecureString *** -AsPlainText -Force", window.Output.Lines.Select(line => line.Text));
        Assert.DoesNotContain(window.Output.Lines, line => line.Text.Contains("Sup3r", StringComparison.Ordinal));

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
