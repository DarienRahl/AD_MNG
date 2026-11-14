using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Directory;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Testing;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// What step 4 <em>Sprawdzenie</em> of the bulk creation wizard says in the status line of the batch editor (track V, stage
/// V2b-4; docs/dev/v-interface-renewal.md "V2b-4 as built"), pl-PL, on the bulk-create sample through <see cref="B4Wizard"/>:
/// the check with the part done and Stop - whoever asked for it -, the summary, what lasts (why there are no rows, stopped,
/// out of date) with its action, the end of the session, and nothing of the sheet in a log copy. A slow domain controller is a
/// call held in the fake session and released deliberately. How rows are computed is covered, unchanged, by
/// <see cref="BulkCheckTests"/>.
/// </summary>
public sealed class BulkCheckStatusTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private const string SummaryOfFive = "Wiersze: 5 – OK: 5, z ostrzeżeniami: 0, z błędami: 0, wykluczone: 0";

    [Fact]
    public async Task TheCheck_SaysHowFarItIs_AndEndsAsTheSummary_WithoutAnythingOfTheSheetInALogCopy()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        var seen = new StatusRecorder(wizard.Editor.Status);
        var logs = new List<string>();
        wizard.Editor.Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                logs.Add(wizard.Editor.Status.Current.ToString());
            }
        };

        await wizard.EnterCheckAsync();

        Assert.Equal(
            [
                (PageStatusKind.Working, "Sprawdzanie wierszy…"),
                (PageStatusKind.Working, "Sprawdzanie wierszy: 0 z 5…"),
                (PageStatusKind.Working, "Sprawdzanie wierszy: 5 z 5…"),
                (PageStatusKind.Idle, SummaryOfFive),
            ],
            seen.Plain);

        // The ring shows the part done from the first row; the count is a new text of the same message.
        Assert.Equal([null, 0d, 1d, null], seen.Seen.Select(s => s.Fraction).ToArray());
        Assert.Equal(wizard.Check.Summary, wizard.Editor.Status.Current.Message);
        Assert.Same(wizard.Check.Status, wizard.Bulk.Status.Front);
        Assert.False(wizard.Check.Status.IsWorking);
        Assert.False(wizard.Editor.Status.CloseCommand.CanExecute(null));
        Assert.Contains("Working: BulkCheck.Status.Checking (5, 5)", logs);
        Assert.Equal("Idle", logs[^1]);
        Assert.DoesNotContain(
            logs,
            log => log.Contains("Wójcik", StringComparison.Ordinal) || log.Contains("lukasz", StringComparison.OrdinalIgnoreCase) || log.Contains("kd20", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCheck_SaysItsWork_InEnglish()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Harness.Localizer.SetCulture(English);
        wizard.Use(wizard.OperatorInput());
        var seen = new StatusRecorder(wizard.Editor.Status);

        await wizard.EnterCheckAsync();

        Assert.Equal(
            [
                (PageStatusKind.Working, "Checking the rows…"),
                (PageStatusKind.Working, "Checking the rows: 0 of 5…"),
                (PageStatusKind.Working, "Checking the rows: 5 of 5…"),
                (PageStatusKind.Idle, wizard.Check.Summary),
            ],
            seen.Plain);
        Assert.StartsWith("Rows: 5 – ", wizard.Check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_ReturnsControlAtOnce_TheStoppedCheckLasts_AndItsActionChecksAgain()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        using var held = new HeldDirectoryCall(wizard.Harness.Session);
        var seen = new StatusRecorder(wizard.Editor.Status);

        var checking = wizard.EnterCheckAsync();
        await held.Reached.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Working, with Stop in the line; nothing says "idle" in between, and the grid is not there yet.
        var working = wizard.Editor.Status.Current;
        Assert.Equal((PageStatusKind.Working, "Sprawdzanie wierszy…"), (working.Kind, working.Message));
        Assert.Equal("BulkCheck.Status.CheckingStart", working.Log.ToString());
        var stop = Assert.IsType<PageStatusAction>(working.Action);
        Assert.Equal("Zatrzymaj", stop.Label);
        Assert.Same(wizard.Check.Status.CancelCommand, stop.Command);
        Assert.True(wizard.Check.IsComputing);
        Assert.True(wizard.Check.IsRechecking);
        Assert.False(wizard.Check.IsRenumbering);

        // "Stopping…" at once - the call that is on its way to the domain controller has not returned.
        stop.Command.Execute(null);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), (wizard.Editor.Status.Current.Kind, wizard.Editor.Status.Current.Message));
        Assert.True(wizard.Check.Status.IsCancelling);
        held.Release();
        await checking.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                (PageStatusKind.Working, "Sprawdzanie wierszy…"),
                (PageStatusKind.Working, "Zatrzymywanie…"),
                (PageStatusKind.Attention, "Sprawdzanie wierszy zatrzymano: wyniki mogą nie odpowiadać danym i wprowadzonym zmianom. Wybierz „Sprawdź ponownie”, aby sprawdzić ponownie."),
            ],
            seen.Plain);
        var stopped = wizard.Editor.Status.Current;
        Assert.Equal("BulkCheck.Status.Stopped", stopped.Log.ToString());
        Assert.False(stopped.CanClose);
        Assert.Same(wizard.Check.Status.Lasting, stopped);
        var recheck = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Sprawdź ponownie", recheck.Label);
        Assert.Same(wizard.Check.RecheckCommand, recheck.Command);
        Assert.False(wizard.Check.IsComputing);
        Assert.False(wizard.Check.IsRechecking);
        Assert.Null(wizard.Check.Computation);
        Assert.False(wizard.Bulk.IsReadyToPlan);
        Assert.Null(wizard.Check.Error);

        // The action checks again: the summary, and nothing that lasts.
        await ((IAsyncRelayCommand)recheck.Command).ExecuteAsync(null);
        Assert.Equal((PageStatusKind.Idle, SummaryOfFive), (wizard.Editor.Status.Current.Kind, wizard.Editor.Status.Current.Message));
        Assert.Null(wizard.Check.Status.Lasting);
        Assert.True(wizard.Bulk.IsReadyToPlan);
    }

    [Fact]
    public async Task TheStopCommand_IsTheSameStop_AsTheOneOfTheLine()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        using var held = new HeldDirectoryCall(wizard.Harness.Session);
        var checking = wizard.EnterCheckAsync();
        await held.Reached.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.True(wizard.Check.StopCommand.CanExecute(null));

        wizard.Check.StopCommand.Execute(null);

        Assert.Equal("Zatrzymywanie…", wizard.Editor.Status.Current.Message);
        held.Release();
        await checking.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.Equal(PageStatusKind.Attention, wizard.Editor.Status.Current.Kind);
        Assert.Equal("BulkCheck.Status.Stopped", wizard.Editor.Status.Current.Log.ToString());
    }

    [Fact]
    public async Task NumerujOdNowa_IsTheCancelOfTheCheckItStarted_AndSprawdzPonowniOfTheOthers()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        var check = wizard.Check;

        // A recheck asked for by the button, and then by an edit of a cell: Sprawdź ponownie runs for both.
        using (var held = new HeldDirectoryCall(wizard.Harness.Session))
        {
            check.Lookups.Refresh();
            var again = check.RecheckCommand.ExecuteAsync(null);
            await held.Reached.WaitAsync(Limit, TestContext.Current.CancellationToken);
            Assert.True(check.IsRechecking);
            Assert.False(check.IsRenumbering);
            Assert.True(check.Status.IsWorking);
            held.Release();
            await again.WaitAsync(Limit, TestContext.Current.CancellationToken);
        }

        Assert.False(check.IsRechecking);
        Assert.False(check.IsRenumbering);

        // Numeruj od nowa: its own flag, and its own Cancel.
        using var heldAgain = new HeldDirectoryCall(wizard.Harness.Session);
        check.Lookups.Refresh();
        var renumbering = check.RenumberCommand.ExecuteAsync(null);
        await heldAgain.Reached.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.True(check.IsRenumbering);
        Assert.False(check.IsRechecking);
        check.Status.CancelCommand.Execute(null);
        Assert.True(check.Status.IsCancelling);
        heldAgain.Release();
        await renumbering.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.Equal(PageStatusKind.Attention, wizard.Editor.Status.Current.Kind);
        Assert.False(check.IsRenumbering);
    }

    [Fact]
    public async Task ACheckThatFails_IsTheStateOfThePage_WithTheReasonTheCodeAndRetry_UntilItIsCheckedAgain()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        var loc = wizard.Harness.Localizer;
        var fail = true;
        wizard.Harness.Session.BeforeOperation = operation =>
        {
            if (fail && operation.Kind == FakeOperationKind.Get)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "81: Server Down, CN=Łukasz Żółć") { LdapResultCode = 81 };
            }
        };
        var seen = new StatusRecorder(wizard.Editor.Status);

        await wizard.EnterCheckAsync();

        // What was tried: why. What to do. (code) - never the text of the server.
        var reason = wizard.Harness.Errors.Translate(DirectoryErrorKind.ServerUnavailable);
        Assert.Equal(PageStatusKind.Working, seen.Plain[0].Kind);
        var failed = wizard.Editor.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal($"Nie udało się sprawdzić wierszy: {reason} {loc[L.Feedback.RetryNext]} (LDAP 81)", failed.Message);
        Assert.Equal("BulkCheck.Status.Failed [LDAP 81]", failed.Log.ToString());
        Assert.DoesNotContain("Żółć", failed.ToString(), StringComparison.Ordinal);
        Assert.False(failed.CanClose);
        Assert.Same(wizard.Check.Status.Lasting, failed);
        Assert.Equal(reason, wizard.Check.Error);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal("Ponów", retry.Label);
        Assert.Same(wizard.Check.RecheckCommand, retry.Command);
        Assert.False(wizard.Bulk.IsReadyToPlan);
        Assert.False(wizard.Check.IsComputing);

        // Retry, pressed in the line, checks again.
        fail = false;
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(null);
        wizard.Harness.Session.BeforeOperation = null;
        Assert.Null(wizard.Check.Status.Lasting);
        Assert.Null(wizard.Check.Error);
        Assert.Equal((PageStatusKind.Idle, SummaryOfFive), (wizard.Editor.Status.Current.Kind, wizard.Editor.Status.Current.Message));
    }

    [Fact]
    public async Task EarlierStepsThatAreIncomplete_AreWhyTheRowsWereNotChecked_WithoutAnAction()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Check.Input = () => null;

        await wizard.EnterCheckAsync();

        var loc = wizard.Harness.Localizer;
        var refused = wizard.Editor.Status.Current;
        Assert.Equal(PageStatusKind.Error, refused.Kind);
        Assert.Equal($"Nie sprawdzono wierszy: {loc[L.BulkCreateCheck.InputIncomplete]}", refused.Message);
        Assert.Equal("BulkCheck.Status.NotChecked", refused.Log.ToString());
        Assert.False(refused.CanClose);
        Assert.Null(refused.Action);
        Assert.Equal(loc[L.BulkCreateCheck.InputIncomplete], wizard.Check.Error);
        Assert.False(wizard.Bulk.IsReadyToPlan);

        // Completed: the next check takes the sentence off.
        wizard.Use(wizard.OperatorInput());
        await wizard.Check.RecheckCommand.ExecuteAsync(null);
        Assert.Null(wizard.Check.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, wizard.Editor.Status.Current.Kind);
    }

    [Fact]
    public async Task WhenTheRowsMayBeOutOfDate_ThePageSaysSo_InTheLanguageOfTheMoment_UntilItIsCheckedAgain()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();

        // An earlier step changed (the container, a rule, the data): the grid is of the data before.
        wizard.Check.OnInputChanged();

        var stale = wizard.Editor.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stale.Kind);
        Assert.Equal(
            "Dane, reguły lub utworzone konta zmieniły się od tego sprawdzenia: wiersze mogą być nieaktualne. Wybierz „Sprawdź ponownie”, aby sprawdzić ponownie.",
            stale.Message);
        Assert.Equal("BulkCheck.Status.Stale", stale.Log.ToString());
        Assert.False(stale.CanClose);
        Assert.Equal("Sprawdź ponownie", stale.Action!.Label);
        Assert.Same(wizard.Check.RecheckCommand, stale.Action.Command);

        wizard.Harness.Localizer.SetCulture(English);
        Assert.Equal(
            "The data, the rules or the created accounts changed since this check: the rows may be out of date. Press “Check again” to check again.",
            wizard.Editor.Status.Current.Message);
        Assert.Equal("Check again", wizard.Editor.Status.Current.Action!.Label);

        await ((IAsyncRelayCommand)wizard.Editor.Status.Current.Action!.Command).ExecuteAsync(null);
        Assert.Null(wizard.Check.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, wizard.Editor.Status.Current.Kind);
        Assert.Equal(wizard.Check.Summary, wizard.Editor.Status.Current.Message);
    }

    [Fact]
    public async Task AnEdit_ThatReplacesTheComputationOfBefore_SaysNothingAboutIt()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        var check = wizard.Check;
        var seen = new StatusRecorder(wizard.Editor.Status);

        check.Rows[1].Cell(Attr.SamAccountName)!.EditText = "kd300";
        await B2Harness.WaitUntilAsync(() => !check.IsComputing && check.Computation!.Rows[1].Value(Attr.SamAccountName) == "kd300");

        // Quiet recomputes: no stopped state, no failure - the rows are the rows of the edit.
        Assert.DoesNotContain(seen.Plain, s => s.Kind is PageStatusKind.Attention or PageStatusKind.Error);
        Assert.Null(check.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, wizard.Editor.Status.Current.Kind);
        Assert.Equal(check.Summary, wizard.Editor.Status.Current.Message);
    }

    [Fact]
    public async Task APlanAfterAStoppedRecompute_ComputesAgain_SoItHasTheChoiceThatStartedIt()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.Input(
            [["Łukasz", "Wójcik", "Piotr Zieliński"], ["Zofia", "Kamińska", "Ewa Wiśniewska"]],
            [.. B4Wizard.NameMappings, new(2, ColumnTargetKind.Manager)]));
        await wizard.EnterCheckAsync();
        var check = wizard.Check;
        var before = check.Computation;
        using var held = new HeldDirectoryCall(wizard.Harness.Session);

        // The choice of one of two managers starts a recompute that reads the directory; the operator stops it there.
        check.Rows[0].Cell(BulkCheckViewModel.ManagerKey)!.ChosenCandidate = wizard.Sample.PiotrZielinski2;
        await held.Reached.WaitAsync(Limit, TestContext.Current.CancellationToken);
        check.StopCommand.Execute(null);
        held.Release();
        await B2Harness.WaitUntilAsync(() => !check.IsComputing);

        // What is shown is the grid of before the choice, and the line says that it may not match.
        Assert.Same(before, check.Computation);
        Assert.Null(check.Computation!.Rows[0].ManagerDn);
        Assert.Equal("BulkCheck.Status.Stopped", wizard.Editor.Status.Current.Log.ToString());
        Assert.False(check.HasPendingRecompute);

        // The plan step does not take that grid as it is: it computes again, with the choice.
        await check.EnsureCurrentAsync(TestContext.Current.CancellationToken);

        Assert.NotSame(before, check.Computation);
        Assert.True(Core.Ldap.DistinguishedName.AreEqual(wizard.Sample.PiotrZielinski2, check.Computation!.Rows[0].ManagerDn!));
        Assert.Null(check.Status.Lasting);
    }

    [Fact]
    public async Task WhenTheSessionEnds_DuringACheck_TheLineGoesStraightToTheEndOfTheSession()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        using var held = new HeldDirectoryCall(wizard.Harness.Session);
        var checking = wizard.EnterCheckAsync();
        await held.Reached.WaitAsync(Limit, TestContext.Current.CancellationToken);
        var seen = new StatusRecorder(wizard.Editor.Status);

        await wizard.Harness.Sessions.DisconnectAsync();

        // The wizard ended what it was doing after the window said why: no summary in between.
        Assert.Equal([(PageStatusKind.Error, wizard.Harness.Localizer[L.WindowSession.Ended])], seen.Plain);
        Assert.True(wizard.Editor.IsSessionEnded);
        Assert.False(wizard.Check.Status.IsWorking);
        Assert.False(wizard.Check.IsRechecking);
        held.Release();
        await checking.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // The call that was on its way returns into nothing: no "stopped", no failure of the check.
        Assert.Equal([(PageStatusKind.Error, wizard.Harness.Localizer[L.WindowSession.Ended])], seen.Plain);
        Assert.Null(wizard.Check.Status.Lasting);
        Assert.Null(wizard.Check.Error);
        Assert.False(wizard.Check.IsComputing);
        Assert.False(wizard.Editor.Status.CloseCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClosingTheWindow_DuringACheck_EndsItWithoutAMessage()
    {
        var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        using var held = new HeldDirectoryCall(wizard.Harness.Session);
        var checking = wizard.EnterCheckAsync();
        await held.Reached.WaitAsync(Limit, TestContext.Current.CancellationToken);
        var status = wizard.Check.Status;
        Assert.True(status.IsWorking);

        wizard.Dispose();
        held.Release();
        await checking.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.False(status.IsWorking);
        Assert.Equal(PageStatusKind.Idle, status.Current.Kind);
        Assert.Null(status.Lasting);
    }
}
