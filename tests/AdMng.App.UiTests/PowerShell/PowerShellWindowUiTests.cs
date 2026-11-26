using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.PowerShell;
using AdMng.App.UiTests.GroupPolicy;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Presentation.Lists;
using CommunityToolkit.Mvvm.Input;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.PowerShell;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests.PowerShell;

/// <summary>
/// The PowerShell window (track R, stage R1; docs/dev/r1-powershell-window.md 7 and 12.4), through the real window, the real engine
/// (a runspace of the test process) and the fake directory: it opens from the connection window and runs a command, its status line
/// says what the command does and Stop stops it, a distinguished name in the output opens the object, <c>Out-AdmGrid</c> fills the grid,
/// <c>Export-AdmList</c> writes only where it is told to, the policies of the workstation are said before the first command, and
/// the trusted mode asks first. A command that waits for the directory is a call the test holds on a pool thread and lets go when it
/// has looked; every state that is asserted lasts until the test changes it.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PowerShellWindowUiTests
{
    [Fact]
    public Task TheWindow_OpensFromTheConnection_ShowsItsConnection_RunsACommand_AndBindsWithoutErrors() => WpfTestHost.RunAsync(async () =>
    {
        var (context, dialogs, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var vm = window.ViewModel;

        // One window of the connection, with its domain in the title; a second request activates it.
        Assert.Contains("contoso.test", window.Title, StringComparison.Ordinal);
        Assert.Contains("PowerShell", window.Title, StringComparison.Ordinal);
        await dialogs.ShowPowerShellAsync();
        await UiTestSupport.WaitForIdleAsync();
        Assert.Same(window, Assert.Single(Application.Current.Windows.OfType<PowerShellWindow>()));

        // The status line at the top says where the window works; the layout has the place of the plan R2 will keep, collapsed.
        var line = Assert.Single(UiWait.Descendants<StatusLine>(window));
        Assert.Same(window.PageStatus, line);
        Assert.Same(vm.Status.Current, line.Status);
        Assert.Contains("contoso.test", line.Message, StringComparison.Ordinal);
        Assert.Contains("ConstrainedLanguage", line.Message, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, window.PendingPlanArea.Visibility);

        // 1 + 1: the command is echoed, the result is a line "2", the editor is empty again.
        await PowerShellUiSupport.RunAsync(window, "1 + 1");
        var lines = PowerShellUiSupport.Lines(window);
        Assert.Contains("PS> 1 + 1", lines);
        Assert.Contains("2", lines);
        Assert.Equal(string.Empty, window.EditorControl.Box.Text);
        Assert.Equal(string.Empty, vm.Input.Text);
        Assert.Equal(PageStatusKind.Done, vm.Status.Current.Kind);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheNamesOfTheControls_AreForAScreenReader_AndTheOrderOfTheWindowIsStatusNotesOutputEditor() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;

        Assert.Equal("Command line", AutomationProperties.GetName(window.EditorControl.Box));
        Assert.Equal("Output of the commands", AutomationProperties.GetName(window.Output));
        Assert.Equal("Run", window.RunButton.Content);
        Assert.Equal("Trusted mode (FullLanguage)", window.TrustedSwitch.Content);
        Assert.True(Top(window.PageStatus, window) < Top(window.Output, window), "the status line is not above the output");
        Assert.True(Top(window.Output, window) < Top(window.EditorControl, window), "the output is not above the editor");
        Assert.True(window.EditorControl.Box.IsTabStop);

        // The empty editor says how to use it, in the words of the language.
        Assert.StartsWith("Type a command", window.EditorControl.Hint, StringComparison.Ordinal);

        // A line of an error, a warning or the note is heard with its kind first (audit W-07); any other line is its text.
        var output = window.ViewModel.Output;
        output.Add([new AdMng.Core.PowerShell.PowerShellTextItem(AdMng.Core.PowerShell.PowerShellStream.Error, "Get-AdmUser: no such user\n")]);
        output.Add([new AdMng.Core.PowerShell.PowerShellTextItem(AdMng.Core.PowerShell.PowerShellStream.Warning, "Only 10 windows\n")]);
        output.AddLine(PowerShellLineKind.PolicyNote, "Transcription is on");
        output.AddLine(PowerShellLineKind.Information, "plain");
        await UiTestSupport.WaitForIdleAsync();
        string NameOf(string text)
        {
            var line = output.Lines.Last(l => l.Text == text);
            window.Output.ScrollIntoView(line);
            window.Output.UpdateLayout();
            return AutomationProperties.GetName((DependencyObject)window.Output.ItemContainerGenerator.ContainerFromItem(line));
        }

        Assert.Equal("Error: Get-AdmUser: no such user", NameOf("Get-AdmUser: no such user"));
        Assert.Equal("Warning: Only 10 windows", NameOf("Only 10 windows"));
        Assert.Equal("Note: Transcription is on", NameOf("Transcription is on"));
        Assert.Equal("plain", NameOf("plain"));
        context.AssertHealthy();
    });

    [Fact]
    public Task AllTheWindowsTexts_FollowThePolishLanguage() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync(language: "pl");
        await using var _c = context;

        Assert.Equal("Wiersz poleceń", AutomationProperties.GetName(window.EditorControl.Box));
        Assert.Equal("Uruchom", window.RunButton.Content);
        Assert.StartsWith("Wpisz polecenie", window.EditorControl.Hint, StringComparison.Ordinal);
        await PowerShellUiSupport.RunAsync(window, "2 * 21");
        Assert.Contains("42", PowerShellUiSupport.Lines(window));
        context.AssertHealthy();
    });

    // ---- The editor ------------------------------------------------------------------------------------------------------

    [Fact]
    public Task TheEditor_DrawsTheTokensInTheColoursOfTheTheme_AndTheModelFollowsTheBox() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var editor = window.EditorControl;

        editor.Box.Text = "Get-AdmUser -Name 'jan' # who";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        Assert.Equal("Get-AdmUser -Name 'jan' # who", window.ViewModel.Input.Text);
        Assert.Equal(string.Concat(editor.Layer.Inlines.OfType<Run>().Select(run => run.Text)), window.ViewModel.Input.Text);
        var runs = editor.Layer.Inlines.OfType<Run>().ToList();
        Assert.True(runs.Count >= 5, $"the text is drawn in {runs.Count} piece(s)");

        // The command is in the accent colour of the text, the string in the caution colour; both are the theme's brushes (they follow a switch of the theme).
        Assert.Same(editor.FindResource("AccentTextFillColorPrimaryBrush"), runs.Single(run => run.Text == "Get-AdmUser").Foreground);
        Assert.Same(editor.FindResource("SystemFillColorCautionBrush"), runs.Single(run => run.Text == "'jan'").Foreground);

        // The box draws nothing of its own text: the coloured text under it is what is seen.
        Assert.Equal(System.Windows.Media.Colors.Transparent, ControlHost.ColorOf(editor.Box.Foreground));
        context.AssertHealthy();
    });

    [Fact]
    public Task Tab_Completes_OneMatchIsTaken_SeveralOpenAListThatEscCloses_AndEnterTakesTheSelected() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var editor = window.EditorControl;
        var vm = window.ViewModel;

        // One match: put into the text at once.
        editor.Box.Text = "Get-AdmUs";
        editor.Box.CaretIndex = editor.Box.Text.Length;
        Assert.True(editor.HandleKey(Key.Tab, ModifierKeys.None));
        await UiWait.UntilAsync(() => editor.Box.Text == "Get-AdmUser", "the one match is in the text");
        Assert.Equal("Get-AdmUser", vm.Input.Text);

        // Several: a list under the caret, the first selected; Down selects the next; Esc closes it and keeps the text.
        editor.Box.Text = "Get-Adm";
        editor.Box.CaretIndex = editor.Box.Text.Length;
        Assert.True(editor.HandleKey(Key.Tab, ModifierKeys.None));
        await UiWait.UntilAsync(() => vm.Input.IsCompletionOpen, "the list of completions is open");
        await UiWait.UntilAsync(() => editor.CompletionHost.IsOpen, "the popup is open");
        Assert.True(editor.Completions.Items.Count >= 6, $"{editor.Completions.Items.Count} completions");
        Assert.Equal(0, editor.Completions.SelectedIndex);
        Assert.True(editor.HandleKey(Key.Down, ModifierKeys.None));
        Assert.Equal(1, editor.Completions.SelectedIndex);
        Assert.True(editor.HandleKey(Key.Escape, ModifierKeys.None));
        Assert.False(vm.Input.IsCompletionOpen);
        Assert.False(editor.CompletionHost.IsOpen);
        Assert.Equal("Get-Adm", editor.Box.Text);

        // Enter takes the selected match - and does not run the command.
        editor.HandleKey(Key.Tab, ModifierKeys.None);
        await UiWait.UntilAsync(() => vm.Input.IsCompletionOpen, "the list is open again");
        editor.HandleKey(Key.Down, ModifierKeys.None);
        var chosen = vm.Input.Completions[vm.Input.SelectedCompletionIndex].Text;
        Assert.True(editor.HandleKey(Key.Enter, ModifierKeys.None));
        await UiWait.UntilAsync(() => editor.Box.Text == chosen, "the selected match is in the text");
        Assert.False(vm.IsRunning);
        Assert.DoesNotContain(PowerShellUiSupport.Lines(window), text => text.StartsWith("PS> Get-Adm", StringComparison.Ordinal));
        context.AssertHealthy();
    });

    [Fact]
    public Task EnterRunsACompleteInput_AnIncompleteOneGetsANewLine_AndShiftEnterAddsOneAlways() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var editor = window.EditorControl;
        var vm = window.ViewModel;

        // An open pipe is not complete: Enter adds a line.
        editor.Box.Text = "Get-AdmVersion |";
        editor.Box.CaretIndex = editor.Box.Text.Length;
        Assert.True(editor.HandleKey(Key.Enter, ModifierKeys.None));
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.False(vm.IsRunning);
        Assert.True(vm.Input.IsMultiLine);
        Assert.Contains('\n', vm.Input.Text);

        // Shift+Enter adds one to a complete input as well, at the caret.
        editor.Box.Text = "1 + 1";
        editor.Box.CaretIndex = 1;
        Assert.True(editor.HandleKey(Key.Enter, ModifierKeys.Shift));
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("1\n + 1", vm.Input.Text);
        Assert.Equal(2, editor.Box.LineCount);
        Assert.DoesNotContain(PowerShellUiSupport.Lines(window), text => text.StartsWith("PS>", StringComparison.Ordinal));

        // A complete one runs.
        editor.Box.Text = "1 + 2";
        Assert.True(editor.HandleKey(Key.Enter, ModifierKeys.None));
        await UiWait.UntilAsync(() => PowerShellUiSupport.Lines(window).Contains("3"), "the command ran");
        context.AssertHealthy();
    });

    [Fact]
    public Task UpAndDown_WalkThroughTheHistory_OfThisWindowOnly() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var editor = window.EditorControl;
        await PowerShellUiSupport.RunAsync(window, "1 + 1");
        await PowerShellUiSupport.RunAsync(window, "2 + 2");

        editor.Box.Text = "draft";
        Assert.True(editor.HandleKey(Key.Up, ModifierKeys.None));
        Assert.Equal("2 + 2", editor.Box.Text);
        Assert.True(editor.HandleKey(Key.Up, ModifierKeys.None));
        Assert.Equal("1 + 1", editor.Box.Text);
        Assert.True(editor.HandleKey(Key.Down, ModifierKeys.None));
        Assert.Equal("2 + 2", editor.Box.Text);
        Assert.True(editor.HandleKey(Key.Down, ModifierKeys.None));
        Assert.Equal("draft", editor.Box.Text);
        context.AssertHealthy();
    });

    [Fact]
    public Task APlainTextPassword_NeverEntersTheHistory() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var editor = window.EditorControl;

        await PowerShellUiSupport.RunAsync(window, "ConvertTo-SecureString 'tajne-hasło' -AsPlainText -Force | Out-Null");
        await PowerShellUiSupport.RunAsync(window, "3 + 3");

        Assert.Equal(["3 + 3"], window.ViewModel.History.Entries);
        editor.Box.Text = string.Empty;
        editor.HandleKey(Key.Up, ModifierKeys.None);
        Assert.Equal("3 + 3", editor.Box.Text);
        context.AssertHealthy();
    });

    /// <summary>
    /// Audit W-notes-undo-stack: the command line keeps an undo step for every text the model puts into it, so after Enter Ctrl+Z
    /// brought back the line that had run - also a line the history refuses because it holds a password in plain text - and after a
    /// question the answer under the text typed ahead. The model's emptying of the line and its change of mode now leave nothing to
    /// undo; what the operator types stays undoable (the first assertions show that the box records it at all).
    /// </summary>
    [Fact]
    public Task CtrlZ_BringsBackNoLineThatRan_NorTheAnswerToAQuestion() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var editor = window.EditorControl;
        var vm = window.ViewModel;

        // A line with a password in plain text: it runs, and the history refuses it.
        editor.Box.Text = "ConvertTo-SecureString 'tajne-hasło' -AsPlainText -Force | Out-Null";
        Assert.True(editor.Box.CanUndo, "the box records the typed text");
        await vm.RunCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Empty(vm.History.Entries);
        Assert.Equal(string.Empty, editor.Box.Text);

        Assert.False(editor.Box.CanUndo);
        ApplicationCommands.Undo.Execute(null, editor.Box);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(string.Empty, editor.Box.Text);
        Assert.Equal(string.Empty, vm.Input.Text);

        // A question that comes while the next command is typed: after the answer the typed-ahead text is back, and Ctrl+Z does not
        // bring the answer back under it.
        editor.Box.Text = "$a = Read-Host 'Name'; 'got ' + $a";
        var run = vm.RunCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => vm.IsRunning, "the command runs");
        editor.Box.Text = "Get-AdmVersion";
        await UiWait.UntilAsync(() => vm.PendingPrompt is not null, "the question");
        await UiTestSupport.WaitForIdleAsync();
        editor.Box.Text = "Łukasz Żółć";
        Assert.True(editor.HandleKey(Key.Enter, ModifierKeys.None));
        await run;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Contains("got Łukasz Żółć", PowerShellUiSupport.Lines(window));
        Assert.Equal("Get-AdmVersion", editor.Box.Text);

        Assert.False(editor.Box.CanUndo);
        ApplicationCommands.Undo.Execute(null, editor.Box);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("Get-AdmVersion", editor.Box.Text);
        Assert.Equal("Get-AdmVersion", vm.Input.Text);

        // What the operator types after that is the box's own undo, as before.
        editor.Box.SelectedText = " ";
        Assert.True(editor.Box.CanUndo);
        context.AssertHealthy();
    });

    /// <summary>
    /// Audit W-08 and W-09: the secret went through the model's text, a string per key, and stayed in the hidden password box after the
    /// answer; text typed ahead of a question stayed in the hidden command line while the model had none. Enter in the password box now
    /// hands over a SecureString, the box is empty after the answer, and the command line shows what was typed ahead again.
    /// </summary>
    [Fact]
    public Task ASecretQuestion_TakesTheSecretFromThePasswordBoxOnly_EmptiesIt_AndTheTypedAheadTextComesBack() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var editor = window.EditorControl;
        var vm = window.ViewModel;

        editor.Box.Text = "$s = Read-Host 'Secret' -AsSecureString; \"length=$($s.Length)\"";
        var run = vm.RunCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => vm.IsRunning, "the command runs");
        editor.Box.Text = "Get-AdmVersion";
        await UiWait.UntilAsync(() => vm.PendingPrompt is not null, "the secret question");
        await UiTestSupport.WaitForIdleAsync();

        Assert.Equal(Visibility.Visible, editor.Secret.Visibility);
        Assert.Equal(Visibility.Collapsed, editor.Box.Visibility);
        editor.Secret.Password = "S3cret!";
        Assert.Equal(string.Empty, vm.Input.Text);

        Assert.True(editor.HandleKey(Key.Enter, ModifierKeys.None));
        await run;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        Assert.Contains("length=7", PowerShellUiSupport.Lines(window));
        Assert.DoesNotContain(PowerShellUiSupport.Lines(window), line => line.Contains("S3cret", StringComparison.Ordinal));
        Assert.Equal(string.Empty, editor.Secret.Password);
        Assert.Equal(Visibility.Collapsed, editor.Secret.Visibility);
        Assert.Equal("Get-AdmVersion", editor.Box.Text);
        Assert.Equal("Get-AdmVersion", vm.Input.Text);
        context.AssertHealthy();
    });

    /// <summary>
    /// Owner answer 11 (audit W-notes-ctrlc-secret): Ctrl+C in the password box of a secret question did nothing, while the guide says
    /// Ctrl+C stops the command. It stops it now, and what was typed of the secret is thrown away at once.
    /// </summary>
    [Fact]
    public Task CtrlC_AtASecretQuestion_StopsTheCommand_AndEmptiesThePasswordBox() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var editor = window.EditorControl;
        var vm = window.ViewModel;

        editor.Box.Text = "$s = Read-Host 'Secret' -AsSecureString; 'after the question'";
        var run = vm.RunCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => vm.PendingPrompt is not null, "the secret question");
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Visible, editor.Secret.Visibility);
        editor.Secret.Password = "half-typed";

        Assert.True(editor.HandleKey(Key.C, ModifierKeys.Control));
        Assert.Equal(string.Empty, editor.Secret.Password);
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        await UiWait.UntilAsync(() => !vm.IsRunning && window.PageStatus.Kind != PageStatusKind.Working, "the command stopped");

        Assert.Equal(PageStatusKind.Attention, window.PageStatus.Kind);
        Assert.Null(vm.PendingPrompt);
        Assert.DoesNotContain("after the question", PowerShellUiSupport.Lines(window));
        Assert.DoesNotContain(PowerShellUiSupport.Lines(window), line => line.Contains("half-typed", StringComparison.Ordinal));
        Assert.Equal(Visibility.Collapsed, editor.Secret.Visibility);
        Assert.Equal(Visibility.Visible, editor.Box.Visibility);
        Assert.Equal(string.Empty, editor.Secret.Password);
        context.AssertHealthy();
    });

    [Fact]
    public Task CtrlC_WithoutASelection_StopsTheCommand_AndCtrlL_ClearsTheWindow() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var editor = window.EditorControl;
        await PowerShellUiSupport.RunAsync(window, "'something'");
        Assert.NotEmpty(window.ViewModel.Output.Lines);

        Assert.True(editor.HandleKey(Key.L, ModifierKeys.Control));
        Assert.Empty(window.ViewModel.Output.Lines);
        Assert.Equal(["'something'"], window.ViewModel.History.Entries);

        // With text selected Ctrl+C is the box's own copy.
        editor.Box.Text = "abc";
        editor.Box.SelectAll();
        Assert.False(editor.HandleKey(Key.C, ModifierKeys.Control));
        context.AssertHealthy();
    });

    // ---- A command that runs, and Stop ------------------------------------------------------------------------------------

    [Fact]
    public Task ACommandThatWaitsForTheDirectory_IsWorkingOnTheLine_AndStopStopsIt() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var vm = window.ViewModel;
        var line = window.PageStatus;
        using var held = new HeldRead(context.Directory.Session, operation => operation.Kind == FakeOperationKind.Search);

        window.EditorControl.Box.Text = "Get-AdmUser -Identity jan.kowalski";
        var running = vm.RunCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => held.IsReached && line.Kind == PageStatusKind.Working, "the command is working on the line");

        // The button is the Cancel of the command, the command line stays usable for the next one.
        Assert.True(vm.IsRunning);
        Assert.True(window.RunButton.IsRunning);
        Assert.True(line.HasAction);
        Assert.Empty(PowerShellUiSupport.Lines(window, PowerShellLineKind.Error));

        // Ctrl+C with nothing selected is Stop: "Stopping…" lasts until the held call is let go.
        Assert.True(window.EditorControl.HandleKey(Key.C, ModifierKeys.Control));
        await UiWait.UntilAsync(() => vm.Status.IsCancelling && line.Kind == PageStatusKind.Working, "the line says it is stopping");
        Assert.True(window.RunButton.IsCancelling);

        held.Release();
        await running;
        await UiWait.UntilAsync(() => !vm.IsRunning && line.Kind != PageStatusKind.Working, "the command ended");
        Assert.Equal(PageStatusKind.Attention, line.Kind);
        Assert.False(window.RunButton.IsRunning);
        context.AssertHealthy();
    });

    [Fact]
    public Task AFloodOfOutput_LeavesTheKeyboardFree_AndCtrlCOfInputPriorityIsTakenAtOnce() => WpfTestHost.RunAsync(async () =>
    {
        // Audit W-03 (by reasoning; this test is the first run of it on a dispatcher): the steps of the stream went to the UI thread at
        // the normal priority, so a command that writes without a pause always had a step queued above the keyboard and the mouse.
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var vm = window.ViewModel;
        window.EditorControl.Box.Text = "while ($true) { 'x' * 40 }";
        var running = vm.RunCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => vm.Output.OutputLinesWritten > 5_000, "the flood reached the window");

        // What a key press is to the dispatcher: an operation of input priority. It is taken while the flood goes on.
        var posted = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? taken = null;
        var stopped = false;
        var press = window.Dispatcher.InvokeAsync(
            () =>
            {
                taken = posted.Elapsed;
                stopped = window.EditorControl.HandleKey(Key.C, ModifierKeys.Control);
            },
            System.Windows.Threading.DispatcherPriority.Input);
        await press.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(stopped);
        Assert.True(taken < TimeSpan.FromSeconds(1), $"Ctrl+C was taken after {taken?.TotalMilliseconds:N0} ms of the flood");

        await running.WaitAsync(TimeSpan.FromSeconds(30));
        await UiWait.UntilAsync(() => !vm.IsRunning && window.PageStatus.Kind != PageStatusKind.Working, "the flood stopped");
        Assert.Equal(PageStatusKind.Attention, window.PageStatus.Kind);
        context.AssertHealthy();
    });

    [Fact]
    public Task ABurstOfChangesQueuesOneScroll_AndAClearBeforeItRunsIsNoError() => WpfTestHost.RunAsync(async () =>
    {
        // Audit W-05: every change of the output queued a scroll that read the last line when it ran, so Ctrl+L between the change and
        // the scroll threw on the UI thread, once per queued scroll.
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var output = window.ViewModel.Output;
        await PowerShellUiSupport.RunAsync(window, "1..50");
        window.Output.ScrollIntoView(window.Output.Items[^1]);
        await UiTestSupport.WaitForIdleAsync();
        var before = window.ScrollRequests;

        // In one turn of the dispatcher: lines arrive three times, then Ctrl+L empties the list before the queued scroll runs.
        output.AddLine(PowerShellLineKind.Information, "one");
        output.AddLine(PowerShellLineKind.Information, "two");
        output.AddLine(PowerShellLineKind.Information, "three");
        Assert.Equal(before + 1, window.ScrollRequests);
        Assert.True(window.EditorControl.HandleKey(Key.L, ModifierKeys.Control));
        await UiTestSupport.WaitForIdleAsync();
        Assert.Empty(output.Lines);

        // The request ran: the next change queues a new one, and it scrolls to the new line.
        output.AddLine(PowerShellLineKind.Information, "four");
        Assert.Equal(before + 2, window.ScrollRequests);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("four", Assert.Single(output.Lines).Text);
        context.AssertHealthy();
    });

    [Fact]
    public Task ACommandThatFails_SaysSoWithTheGlyphOfAnError_AndTheLineSaysItToo() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;

        await PowerShellUiSupport.RunAsync(window, "Get-AdmUser -Identity 'nobody.like.this'");

        var errors = PowerShellUiSupport.Lines(window, PowerShellLineKind.Error);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, text => text.Contains("nobody.like.this", StringComparison.Ordinal));
        Assert.Equal(PageStatusKind.Attention, window.PageStatus.Kind);

        // An error line has its glyph in the item (never the colour alone).
        window.Output.UpdateLayout();
        var glyph = UiWait.Descendants<TextBlock>(window.Output).First(block => block.Name == "KindGlyph" && block.Visibility == Visibility.Visible);
        Assert.Equal(AdMng.App.Controls.Glyphs.Error, glyph.Text);
        context.AssertHealthy();
    });

    // ---- The commands that need the window ------------------------------------------------------------------------------

    [Fact]
    public Task ADistinguishedNameInTheOutput_IsALink_ThatOpensTheObject() => WpfTestHost.RunAsync(async () =>
    {
        var (context, dialogs, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var dn = context.Company.JanKowalski;

        await PowerShellUiSupport.RunAsync(window, $"Write-Output '{dn}'");
        window.Output.UpdateLayout();

        var link = UiWait.Descendants<TextBlock>(window.Output).SelectMany(block => block.Inlines.OfType<Hyperlink>()).Single();
        Assert.False(link.Focusable);
        Assert.NotNull(link.ContextMenu);
        Assert.Equal(["Open in the program", "Copy the distinguished name"], link.ContextMenu.Items.OfType<MenuItem>().Select(item => (string)item.Header));
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent, link));
        await UiWait.UntilAsync(() => dialogs.OpenedProperties.Count == 1, "the object was opened");
        Assert.Equal(dn, dialogs.OpenedProperties[0]);
        context.AssertHealthy();
    });

    [Fact]
    public Task OpenAdmObject_OpensThePropertiesOfTheObject_AndAMissingOneIsAnError() => WpfTestHost.RunAsync(async () =>
    {
        var (context, dialogs, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;

        await PowerShellUiSupport.RunAsync(window, "Open-AdmObject jan.kowalski");
        await UiWait.UntilAsync(() => dialogs.OpenedProperties.Count == 1, "the object was opened");
        Assert.Equal(context.Company.JanKowalski, dialogs.OpenedProperties[0], ignoreCase: true);

        await PowerShellUiSupport.RunAsync(window, "Open-AdmObject nobody.like.this");
        Assert.Single(dialogs.OpenedProperties);
        Assert.Contains(PowerShellUiSupport.Lines(window, PowerShellLineKind.Error), text => text.Contains("nobody.like.this", StringComparison.Ordinal));
        context.AssertHealthy();
    });

    [Fact]
    public Task OutAdmGrid_OpensAGridWithTheObjectsAsRows_ThatSortsFiltersOpensAndExports() => WpfTestHost.RunAsync(async () =>
    {
        var (context, dialogs, files, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;

        await PowerShellUiSupport.RunAsync(window, "Get-AdmUser -Department IT -Properties department, mail | Out-AdmGrid -Title 'IT staff'");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        var grid = Assert.Single(Application.Current.Windows.OfType<PowerShellGridWindow>());
        var model = grid.ViewModel;
        Assert.Equal("IT staff – contoso.test", grid.Title);
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal("DistinguishedName", model.Columns[0].Header);
        Assert.Contains(model.Columns, column => column.Header == "department");

        // The columns of the list are the grid's: one GridViewColumn for each, the header says its name.
        var view = Assert.IsType<GridView>(grid.ResultsList.View);
        Assert.Equal(model.Columns.Count, view.Columns.Count);
        Assert.Same(model.Columns[0], view.Columns[0].Header);
        Assert.Equal(PageStatusKind.Idle, grid.PageStatus.Kind);
        Assert.Equal("2 rows · " + model.Columns.Count + " columns", grid.PageStatus.Message);

        // Filter, sort, open and export, as in any list.
        model.FilterText = "wisniewski";
        await UiWait.UntilAsync(() => model.Rows.Count == 1, "the filter narrowed the rows");
        model.FilterText = string.Empty;
        await UiWait.UntilAsync(() => model.Rows.Count == 2, "the filter was cleared");
        await model.SortCommand.ExecuteAsync(model.Columns[0]);
        Assert.Equal(SortDirection.Ascending, model.Columns[0].SortDirection);

        grid.ResultsList.SelectedItem = model.Rows[0];
        await UiWait.UntilAsync(() => model.OpenCommand.CanExecute(null), "a row with a distinguished name can be opened");
        await model.OpenCommand.ExecuteAsync(null);
        Assert.Equal(model.Rows[0].DistinguishedName, Assert.Single(dialogs.OpenedProperties));

        var path = PowerShellUiSupport.TempFile("grid.csv");
        files.NextSavePath = path;
        try
        {
            await model.ExportCommand.ExecuteAsync(null);
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("DistinguishedName", text, StringComparison.Ordinal);
            Assert.Contains("jan.kowalski", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }

        context.AssertHealthy();
    });

    /// <summary>
    /// Owner answer 12 (audit W-notes-out-admgrid-cap): every call of <c>Out-AdmGrid</c> opened a window, so a loop was a window per
    /// object. Ten grid windows are open at most: the eleventh call is an error and opens none, and a grid the operator closes frees its
    /// place (the program learns it from the window's Closed event). The texts of the error in both languages are the engine tests'.
    /// </summary>
    [Fact]
    public Task OutAdmGrid_OpensAtMostTenGridsAtATime_TheEleventhIsAnError_AndAClosedGridFreesItsPlace() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        static List<PowerShellGridWindow> Grids() => [.. Application.Current.Windows.OfType<PowerShellGridWindow>()];

        await PowerShellUiSupport.RunAsync(
            window,
            "foreach ($i in 1..11) { $i | Out-AdmGrid -Title \"Grid $i\" -ErrorVariable +refused -ErrorAction SilentlyContinue }; " +
            "'refused=' + $refused.Count; $refused[0].FullyQualifiedErrorId.Split(',')[0]");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Equal(10, Grids().Count);
        Assert.DoesNotContain(Grids(), grid => grid.Title.StartsWith("Grid 11", StringComparison.Ordinal));
        Assert.Contains("refused=1", PowerShellUiSupport.Lines(window));
        Assert.Contains("AdmTooManyGrids", PowerShellUiSupport.Lines(window));

        // The operator closes one grid: the next call opens one, and the one after is refused again.
        Grids().First(grid => grid.Title.StartsWith("Grid 3", StringComparison.Ordinal)).Close();
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(9, Grids().Count);
        await PowerShellUiSupport.RunAsync(window, "'x' | Out-AdmGrid -Title 'Again'; 'y' | Out-AdmGrid -Title 'Once more' -ErrorAction SilentlyContinue -ErrorVariable again; 'again=' + $again.Count");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Equal(10, Grids().Count);
        Assert.Contains(Grids(), grid => grid.Title.StartsWith("Again", StringComparison.Ordinal));
        Assert.Contains("again=1", PowerShellUiSupport.Lines(window));
        context.AssertHealthy();
    });

    [Fact]
    public Task ExportAdmList_WritesOnlyToThePathItWasGiven_OrToTheFileTheOperatorChose_AndNothingWithoutOne() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, files, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var path = PowerShellUiSupport.TempFile("list.csv");
        var chosen = PowerShellUiSupport.TempFile("chosen.csv");
        try
        {
            // No path and nobody chose a file: it asks in Save As, writes nothing and says so.
            files.NextSavePath = null;
            await PowerShellUiSupport.RunAsync(window, "Get-AdmUser jan.kowalski | Export-AdmList");
            Assert.Single(files.SaveAsAsked);
            Assert.Contains(PowerShellUiSupport.Lines(window, PowerShellLineKind.Warning), text => text.Contains("nothing was written", StringComparison.Ordinal));
            Assert.False(File.Exists(chosen));

            // A path: exactly that file, with the protection of formulas of the list export.
            await PowerShellUiSupport.RunAsync(window, $"New-Object PSObject -Property @{{ Name = '=1+1'; Mail = 'a@b.c' }} | Export-AdmList -Path '{path}'");
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("'=1+1", text, StringComparison.Ordinal);
            Assert.Contains("a@b.c", text, StringComparison.Ordinal);
            Assert.Single(files.SaveAsAsked);

            // The file exists now: no replacing without -Force.
            await PowerShellUiSupport.RunAsync(window, $"1 | Export-AdmList -Path '{path}'");
            Assert.Contains(PowerShellUiSupport.Lines(window, PowerShellLineKind.Error), error => error.Contains("exists", StringComparison.Ordinal));
            Assert.Contains("a@b.c", await File.ReadAllTextAsync(path), StringComparison.Ordinal);

            // The operator chose a file in Save As.
            files.NextSavePath = chosen;
            await PowerShellUiSupport.RunAsync(window, "Get-AdmUser jan.kowalski | Export-AdmList");
            Assert.Contains("jan.kowalski", await File.ReadAllTextAsync(chosen), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(chosen);
        }

        context.AssertHealthy();
    });

    // ---- What lasts: the policies of the workstation and the trusted mode -----------------------------------------------

    [Fact]
    public Task TheNoteAboutTheRecordingPolicies_IsSaidBeforeTheFirstCommand_InTheLineTheLayoutAndTheScrollback() => WpfTestHost.RunAsync(async () =>
    {
        var source = new ScriptedPolicySource().WithTranscription(@"C:\PSTranscripts");
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync(services => services.Replace(ServiceDescriptor.Singleton<IPowerShellPolicySource>(source)));
        await using var _c = context;
        var vm = window.ViewModel;

        // Nothing was run yet.
        Assert.Empty(vm.History.Entries);
        Assert.Contains(@"C:\PSTranscripts", vm.PolicyNoteText, StringComparison.Ordinal);
        Assert.True(window.PolicyNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.PolicyNote.Style);
        Assert.Contains(UiWait.Descendants<TextBlock>(window.PolicyNote), block => block.Text.Contains(@"C:\PSTranscripts", StringComparison.Ordinal));
        Assert.Equal(PageStatusKind.Attention, window.PageStatus.Kind);
        Assert.Equal(vm.PolicyNoteText, window.PageStatus.Message);
        Assert.Contains(vm.Output.Lines, output => output.Kind == PowerShellLineKind.PolicyNote);
        Assert.False(window.TrustedNote.IsVisible);
        context.AssertHealthy();
    });

    [Fact]
    public Task WithoutARecordingPolicy_ThereIsNoNote() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _, window) = await PowerShellUiSupport.OpenAsync(services =>
            services.Replace(ServiceDescriptor.Singleton<IPowerShellPolicySource>(new ScriptedPolicySource())));
        await using var _c = context;

        Assert.False(window.PolicyNote.IsVisible);
        Assert.Equal(PageStatusKind.Idle, window.PageStatus.Kind);
        Assert.DoesNotContain(window.ViewModel.Output.Lines, output => output.Kind == PowerShellLineKind.PolicyNote);
        context.AssertHealthy();
    });

    [Fact]
    public Task AnEnforcedLockdown_DisablesTheTrustedModeWithTheReason() => WpfTestHost.RunAsync(async () =>
    {
        var source = new ScriptedPolicySource { Lockdown = AdMng.Core.PowerShell.PowerShellLockdownState.Enforce };
        var (context, dialogs, _, window) = await PowerShellUiSupport.OpenAsync(services => services.Replace(ServiceDescriptor.Singleton<IPowerShellPolicySource>(source)));
        await using var _c = context;

        Assert.False(window.TrustedSwitch.IsEnabled);
        Assert.Contains("WDAC", (string)window.TrustedSwitch.ToolTip, StringComparison.Ordinal);
        Assert.Empty(dialogs.Confirmations);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheTrustedMode_AsksFirst_NoLeavesItOff_YesTurnsItOnWithANoteAndTheTitle_AndOffAsksNothing() => WpfTestHost.RunAsync(async () =>
    {
        var (context, dialogs, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var vm = window.ViewModel;
        var titleBefore = window.Title;

        // Constrained: a .NET call is refused.
        await PowerShellUiSupport.RunAsync(window, "[System.IO.Path]::GetFileName('a\\b')");
        Assert.NotEmpty(PowerShellUiSupport.Lines(window, PowerShellLineKind.Error));

        dialogs.ConfirmAnswer = false;
        await ((IAsyncRelayCommand)window.TrustedSwitch.Command).ExecuteAsync(null);
        Assert.Single(dialogs.Confirmations);
        Assert.False(window.TrustedSwitch.IsChecked);
        Assert.False(vm.IsTrustedMode);
        Assert.False(window.TrustedNote.IsVisible);

        dialogs.ConfirmAnswer = true;
        await ((IAsyncRelayCommand)window.TrustedSwitch.Command).ExecuteAsync(null);
        Assert.Equal(2, dialogs.Confirmations.Count);
        Assert.Contains(".NET", dialogs.Confirmations[1], StringComparison.Ordinal);
        Assert.Contains("contoso.test", dialogs.Confirmations[1], StringComparison.Ordinal);
        Assert.True(window.TrustedSwitch.IsChecked);
        Assert.True(vm.IsTrustedMode);
        Assert.True(window.TrustedNote.IsVisible);
        Assert.NotEqual(titleBefore, window.Title);
        Assert.Contains("rusted", window.Title, StringComparison.Ordinal);

        // FullLanguage: the same call works.
        await PowerShellUiSupport.RunAsync(window, "[System.IO.Path]::GetFileName('a\\b')");
        Assert.Contains("b", PowerShellUiSupport.Lines(window));

        // Off needs no question.
        await ((IAsyncRelayCommand)window.TrustedSwitch.Command).ExecuteAsync(null);
        Assert.Equal(2, dialogs.Confirmations.Count);
        Assert.False(window.TrustedSwitch.IsChecked);
        Assert.False(window.TrustedNote.IsVisible);
        Assert.Equal(titleBefore, window.Title);
        context.AssertHealthy();
    });

    // ---- Closing ------------------------------------------------------------------------------------------------------------

    [Fact]
    public Task AnIdleWindow_ClosesWithoutAQuestion_AndItsEngineGoesWithIt() => WpfTestHost.RunAsync(async () =>
    {
        var (context, dialogs, _, window) = await PowerShellUiSupport.OpenAsync();
        await using var _c = context;
        var vm = window.ViewModel;
        await PowerShellUiSupport.RunAsync(window, "1 + 1");

        window.Close();
        await UiWait.UntilAsync(() => vm.IsDisposed, "the window was closed and its view model disposed");

        Assert.Empty(Application.Current.Windows.OfType<PowerShellWindow>());
        Assert.Empty(dialogs.Confirmations);
        context.AssertHealthy();
    });

    private static double Top(FrameworkElement element, UIElement relativeTo) => element.TranslatePoint(new Point(0, 0), relativeTo).Y;
}
