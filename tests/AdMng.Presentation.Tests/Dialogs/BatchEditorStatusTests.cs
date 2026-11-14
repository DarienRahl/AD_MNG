using System.ComponentModel;
using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>
/// The status line of the batch editor (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-1 as built"): one
/// line for the window and for the source of its first step. The source - the selected objects, or the CSV file - says
/// its summary, its read and a read that failed; the window says checking (planning) the changes with the part done,
/// executing them with the part done, and the result with the domain; Stop returns control at once; and nothing of it
/// reaches the diagnostic log but keys and numbers. How plans are made and executed is covered, unchanged, by
/// <see cref="BatchEditorTests"/>.
/// </summary>
public class BatchEditorStatusTests
{
    /// <summary>Longer than any wait of a test: a call that is not cancelled fails the test instead of ending by itself.</summary>
    private static readonly TimeSpan SlowDomainController = TimeSpan.FromSeconds(30);

    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    // ---- The source of the first step: the selected objects -----------------------------------------------------

    [Fact]
    public async Task TheLine_ShowsTheSummaryOfTheSelectedObjects_WhileTheFirstStepIsShown()
    {
        var (editor, _) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski, c.GroupIt]);

        // One status holder for the window; the list of objects is the page in front of it and says the summary.
        Assert.IsAssignableFrom<IHasPageStatus>(editor);
        Assert.NotNull(editor.SourceStatus);
        Assert.Same(editor.SourceStatus, editor.Status.Front);
        Assert.Equal(PageStatusKind.Idle, editor.Status.Current.Kind);
        Assert.Equal("3 objects · User: 2, Group: 1", editor.Status.Current.Message);
        Assert.False(editor.Status.IsWorking);
        Assert.Null(editor.Status.Lasting);
        Assert.Null(editor.Status.Current.Action);
        Assert.False(editor.Status.CloseCommand.CanExecute(null));
        Assert.Equal("User: 2, Group: 1", editor.TargetSummary);
    }

    [Fact]
    public async Task TheSummaryOfTheSelectedObjects_IsPolish_WithItsPluralForms()
    {
        var (three, _) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski, c.GroupIt], Polish);
        Assert.Equal("3 obiekty · Użytkownik: 2, Grupa: 1", three.Status.Current.Message);

        var (one, _) = await OpenAsync(c => [c.JanKowalski], Polish);
        Assert.Equal("1 obiekt · Użytkownik: 1", one.Status.Current.Message);
    }

    [Fact]
    public async Task ReadingTheSelectedObjects_IsAnOperationOfTheList_AndStopReturnsControl()
    {
        var harness = await B2Harness.CreateAsync();
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([harness.Company.JanKowalski, harness.Company.LukaszWisniewski]));
        var source = editor.SourceStatus!;
        harness.Session.Latency = SlowDomainController;
        List<(PageStatusKind Kind, string Message)> seen;
        try
        {
            var opening = editor.InitializeAsync();

            // Working in the line of the window - the operation is the list's, and so is its Stop.
            Assert.True(editor.IsLoadingTargets);
            Assert.True(source.IsWorking);
            Assert.False(editor.Status.IsWorking);
            Assert.Same(source.Current, editor.Status.Current);
            Assert.Equal("Reading the selected objects (2)…", editor.Status.Current.Message);
            Assert.Equal("Batch.Status.ReadingTargets (2)", editor.Status.Current.Log.ToString());
            var stop = Assert.IsType<PageStatusAction>(editor.Status.Current.Action);
            Assert.Equal("Stop", stop.Label);
            Assert.Same(source.CancelCommand, stop.Command);
            Assert.False(editor.ReloadTargetsCommand.CanExecute(null));

            // Pressed: "Stopping…" at once, and the read ends well before the domain controller would have answered.
            seen = Record(editor.Status);
            stop.Command.Execute(stop.Parameter);
            await opening.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        finally
        {
            harness.Session.Latency = TimeSpan.Zero;
        }

        // Stopped: the state of the list for as long as it lasts - what may be missing, and Retry.
        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        var stopped = editor.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(
            "Reading the selected objects was stopped: names, types and attribute suggestions may be missing. Press Retry to read them again.",
            stopped.Message);
        Assert.Equal("Batch.Status.TargetsStopped", stopped.Log.ToString());
        Assert.False(stopped.CanClose);
        Assert.Same(source.Lasting, stopped);
        Assert.False(editor.IsLoadingTargets);
        Assert.False(source.IsWorking);
        Assert.Null(editor.Error);
        var retry = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Retry", retry.Label);
        Assert.Same(editor.ReloadTargetsCommand, retry.Command);

        // Retry reads them: the summary, and nothing that lasts.
        await editor.ReloadTargetsCommand.ExecuteAsync(null);
        Assert.Null(source.Lasting);
        Assert.Equal("2 objects · User: 2", editor.Status.Current.Message);
        Assert.Equal(["Jan Kowalski", "Łukasz Wiśniewski"], editor.Targets.Select(t => t.Name));
    }

    [Fact]
    public async Task ARead_OfTheSelectedObjects_ThatFails_IsTheStateOfTheList_WithRetry()
    {
        var harness = await B2Harness.CreateAsync();
        var fail = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (fail && operation.Kind == FakeOperationKind.Search)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "81: Server Down, CN=Łukasz Żółć") { LdapResultCode = 81 };
            }
        };
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([harness.Company.JanKowalski]));

        await editor.InitializeAsync();

        // What was tried: why. What to do. (code) - never the text of the server.
        var failed = editor.Status.Current;
        var reason = harness.Errors.Translate(DirectoryErrorKind.ServerUnavailable);
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal($"Could not read the selected objects: {reason} Press Retry to try again. (LDAP 81)", failed.Message);
        Assert.Equal("Batch.Status.TargetsFailed [LDAP 81]", failed.Log.ToString());
        Assert.DoesNotContain("Żółć", failed.Message, StringComparison.Ordinal);
        Assert.False(failed.CanClose);
        Assert.Same(editor.SourceStatus!.Lasting, failed);
        Assert.Equal(reason, editor.Error);
        Assert.False(editor.IsLoadingTargets);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal("Retry", retry.Label);

        // The names are not known, but the objects are: the operations can still be planned.
        Assert.Single(editor.Targets);

        fail = false;
        await editor.ReloadTargetsCommand.ExecuteAsync(null);
        Assert.Null(editor.SourceStatus.Lasting);
        Assert.Equal("1 object · User: 1", editor.Status.Current.Message);
    }

    [Fact]
    public async Task Retry_OfTheSelectedObjects_NeverThrows_WhateverTheReadFailsWith()
    {
        var harness = await B2Harness.CreateAsync();
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([harness.Company.JanKowalski]));
        await editor.InitializeAsync();
        Assert.Equal("1 object · User: 1", editor.Status.Current.Message);

        // Not a directory error: the Retry of the line is a command - it says what happened and does not throw.
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Search)
            {
                throw new InvalidOperationException("CN=Łukasz Żółć is broken");
            }
        };
        await editor.ReloadTargetsCommand.ExecuteAsync(null);

        var failed = editor.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith("Could not read the selected objects: ", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Press Retry to try again. (InvalidOperationException)", failed.Message, StringComparison.Ordinal);
        Assert.Equal("Batch.Status.TargetsFailed [InvalidOperationException]", failed.Log.ToString());
        Assert.DoesNotContain("Żółć", failed.ToString(), StringComparison.Ordinal);
        Assert.False(failed.CanClose);
        Assert.False(editor.IsLoadingTargets);
        Assert.False(editor.SourceStatus!.IsWorking);
        Assert.True(editor.ReloadTargetsCommand.CanExecute(null));

        // The objects are still there, as they were read before.
        Assert.Equal(["Jan Kowalski"], editor.Targets.Select(t => t.Name));

        harness.Session.BeforeOperation = null;
        await editor.ReloadTargetsCommand.ExecuteAsync(null);
        Assert.Null(editor.SourceStatus.Lasting);
        Assert.Equal("1 object · User: 1", editor.Status.Current.Message);
    }

    // ---- Checking (planning) the changes -------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Checking the changes…", "Checking the changes: 1 of 3…")]
    [InlineData("pl-PL", "Sprawdzanie zmian…", "Sprawdzanie zmian: 1 z 3…")]
    public async Task CheckingTheChanges_SaysHowFarItIs_AndEndsInTheSummaryOfThePreview(string culture, string start, string oneOfThree)
    {
        var (editor, harness) = await OpenAsync(
            c => [c.JanKowalski, c.LukaszWisniewski, c.MalgorzataWojcik], CultureInfo.GetCultureInfo(culture), new SetAttributeOperation(Attr.Title, "Koordynator V2b"));
        await harness.Settings.UpdateAsync(s => s.BatchMaxParallelism = 1, TestContext.Current.CancellationToken);
        var second = harness.Company.LukaszWisniewski;
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        harness.Session.BeforeOperation = operation =>
        {
            // The planner reads one object after another: the read of the second one waits for the test.
            if (operation.Kind == FakeOperationKind.Get && DnKey.For(operation.DistinguishedName) == DnKey.For(second))
            {
                held.Release();
                release.Wait(TestContext.Current.CancellationToken);
            }
        };
        var seen = Record(editor.Status);
        Task preview;
        try
        {
            preview = editor.PreviewCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));

            // The operation of the window, with the part done; the list of objects keeps its own status behind it.
            Assert.True(editor.IsBusy);
            Assert.True(editor.Status.IsWorking);
            Assert.Equal(PageStatusKind.Working, editor.Status.Current.Kind);
            Assert.Equal(oneOfThree, editor.Status.Current.Message);
            Assert.Equal(1d / 3, editor.Status.Current.Progress!.Value.Fraction!.Value, 5);
            Assert.Equal("Batch.Status.Checking (1, 3)", editor.Status.Current.Log.ToString());
            Assert.Same(editor.Status.CancelCommand, editor.Status.Current.Action!.Command);
            Assert.Equal(1, editor.PlanningCompleted);
            Assert.Equal(3, editor.PlanningTotal);
        }
        finally
        {
            release.Set();
        }

        await preview;
        harness.Session.BeforeOperation = null;

        // First "checking" without a number (the ring turns), then the numbers - one message - and the result is the
        // preview itself: the line shows its summary, and the page in front is gone with the first step.
        Assert.Equal((PageStatusKind.Working, start), seen[0]);
        Assert.Equal((PageStatusKind.Working, oneOfThree), seen[1]);
        Assert.DoesNotContain(seen, s => s.Kind is PageStatusKind.Done or PageStatusKind.Attention or PageStatusKind.Error);
        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Null(editor.Status.Front);
        Assert.False(editor.Status.IsWorking);
        Assert.Equal(PageStatusKind.Idle, editor.Status.Current.Kind);
        Assert.Equal(editor.PreviewSummary, editor.Status.Current.Message);
        Assert.NotEqual(string.Empty, editor.PreviewSummary);

        // The summary follows the include check boxes.
        editor.SetAllIncludedCommand.Execute(false);
        Assert.Equal(editor.PreviewSummary, editor.Status.Current.Message);
        Assert.Equal(3, editor.ExcludedCount);
    }

    [Fact]
    public async Task CheckingTheChanges_AndReadingTheObjects_DoNotReplaceOneAnother()
    {
        var harness = await B2Harness.CreateAsync();
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = 1;
        harness.Session.BeforeOperation = operation =>
        {
            // The search that resolves the names of the selected objects - the first one - waits for the test.
            if (operation.Kind == FakeOperationKind.Search && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Release();
                release.Wait(TestContext.Current.CancellationToken);
            }
        };
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([harness.Company.JanKowalski, harness.Company.LukaszWisniewski])
        {
            Operations = [new SetAttributeOperation(Attr.Title, "Koordynator V2b")],
        });
        var source = editor.SourceStatus!;
        Task opening;
        try
        {
            opening = editor.InitializeAsync();
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            Assert.True(source.IsWorking);
            Assert.Equal("Reading the selected objects (2)…", editor.Status.Current.Message);

            // The operator does not wait for the names: Preview. The check is the operation of the window and takes
            // the line; the read of the objects goes on - it is not replaced.
            Assert.True(editor.PreviewCommand.CanExecute(null));
            await editor.PreviewCommand.ExecuteAsync(null);
            Assert.Equal(BatchEditorStep.Preview, editor.Step);
            Assert.Equal(2, editor.ChangeCount);
            Assert.True(source.IsWorking);
            Assert.True(editor.IsLoadingTargets);
            Assert.Equal(editor.PreviewSummary, editor.Status.Current.Message);
        }
        finally
        {
            release.Set();
        }

        await opening;
        harness.Session.BeforeOperation = null;

        // The names arrived meanwhile; back in the first step the list says its summary again.
        Assert.Equal(["Jan Kowalski", "Łukasz Wiśniewski"], editor.Targets.Select(t => t.Name));
        Assert.False(source.IsWorking);
        Assert.Equal(editor.PreviewSummary, editor.Status.Current.Message);
        editor.BackToEditCommand.Execute(null);
        Assert.Same(source, editor.Status.Front);
        Assert.Equal("2 objects · User: 2", editor.Status.Current.Message);
    }

    [Fact]
    public async Task StopOfACheck_SaysStoppingAtOnce_AndEndsAsAttention_WithRetry()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski], operations: new DeleteOperation());
        harness.Session.Latency = SlowDomainController;
        List<(PageStatusKind Kind, string Message)> seen;
        try
        {
            var preview = editor.PreviewCommand.ExecuteAsync(null);
            Assert.True(editor.IsBusy);
            Assert.True(editor.Status.IsWorking);
            Assert.Equal("Checking the changes…", editor.Status.Current.Message);
            Assert.True(editor.Status.Current.Progress!.Value.IsIndeterminate);
            var stop = Assert.IsType<PageStatusAction>(editor.Status.Current.Action);
            Assert.Equal("Stop", stop.Label);

            seen = Record(editor.Status);
            stop.Command.Execute(stop.Parameter);
            Assert.True(editor.Status.IsCancelling);
            await preview.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        finally
        {
            harness.Session.Latency = TimeSpan.Zero;
        }

        // Control is back: the first step, nothing planned, nothing written - and the line says what was stopped.
        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        Assert.Equal(BatchEditorStep.Edit, editor.Step);
        Assert.False(editor.IsBusy);
        Assert.False(editor.Status.IsWorking);
        Assert.Null(editor.Error);
        Assert.Empty(editor.PreviewObjects);
        Assert.Empty(harness.Session.Writes);
        var stopped = editor.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Checking the changes was stopped: nothing was changed. Press Retry to try again.", stopped.Message);
        Assert.Equal("Batch.Status.CheckStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        var retry = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Retry", retry.Label);
        Assert.Same(editor.PreviewCommand, retry.Command);

        // Retry checks again - the domain controller answers now.
        await editor.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Equal(editor.PreviewSummary, editor.Status.Current.Message);
    }

    [Fact]
    public async Task TheCancelOfThePreviewCommand_IsTheSameStop()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski], Polish, new DeleteOperation());
        harness.Session.Latency = SlowDomainController;
        try
        {
            var preview = editor.PreviewCommand.ExecuteAsync(null);
            Assert.True(editor.PreviewCancelCommand.CanExecute(null));
            editor.PreviewCancelCommand.Execute(null);
            Assert.True(editor.Status.IsCancelling);
            await preview.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        finally
        {
            harness.Session.Latency = TimeSpan.Zero;
        }

        Assert.Equal(PageStatusKind.Attention, editor.Status.Current.Kind);
        Assert.Equal("Sprawdzanie zmian zatrzymano: niczego nie zmieniono. Wybierz Ponów, aby spróbować ponownie.", editor.Status.Current.Message);
        Assert.Equal(BatchEditorStep.Edit, editor.Step);
    }

    [Fact]
    public async Task ACheckThatFails_IsAnError_WithRetry_AndGoesWhenClosed()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski], operations: new SetAttributeOperation(Attr.Title, "Koordynator V2b"));
        var fail = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (fail && operation.Kind == FakeOperationKind.Get)
            {
                throw new InvalidOperationException("CN=Łukasz Żółć is broken");
            }
        };

        await editor.PreviewCommand.ExecuteAsync(null);

        var failed = editor.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith("Could not check the changes: ", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Press Retry to try again. (InvalidOperationException)", failed.Message, StringComparison.Ordinal);
        Assert.Equal("Batch.Status.CheckFailed [InvalidOperationException]", failed.Log.ToString());
        Assert.DoesNotContain("Żółć", failed.ToString(), StringComparison.Ordinal);
        Assert.True(failed.CanClose);
        Assert.Same(editor.PreviewCommand, failed.Action!.Command);
        Assert.NotNull(editor.Error);
        Assert.Equal(BatchEditorStep.Edit, editor.Step);
        Assert.False(editor.IsBusy);

        // The first step is as it was: closed, the line shows the summary of the objects again.
        editor.Status.CloseCommand.Execute(null);
        Assert.Equal("2 objects · User: 2", editor.Status.Current.Message);

        fail = false;
        await editor.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Null(editor.Error);
    }

    // ---- Executing ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(
        "en-US",
        "Executing “Batch edit (3 objects)” in contoso.test: 1 of 3…",
        "“Batch edit (3 objects)” was executed in contoso.test: 3 objects changed. The report is in the list of background operations.")]
    [InlineData(
        "pl-PL",
        "Wykonywanie „Edycja zbiorcza (obiekty: 3)” w domenie contoso.test: 1 z 3…",
        "Wykonano „Edycja zbiorcza (obiekty: 3)” w domenie contoso.test: zmieniono 3 obiekty. Raport jest na liście operacji w tle.")]
    public async Task Executing_SaysHowFarItIs_AndTheResultNamesTheDomain(string culture, string oneOfThree, string done)
    {
        var (editor, harness) = await OpenAsync(
            c => [c.JanKowalski, c.LukaszWisniewski, c.MalgorzataWojcik], CultureInfo.GetCultureInfo(culture), new SetAttributeOperation(Attr.Title, "Koordynator V2b"));
        await harness.Settings.UpdateAsync(s => s.BatchMaxParallelism = 1, TestContext.Current.CancellationToken);
        await editor.PreviewCommand.ExecuteAsync(null);
        Assert.True(editor.IsExecuteStep);
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var writes = 0;
        harness.Session.BeforeOperation = operation =>
        {
            // One object after another: the second write waits for the test.
            if (operation.IsWrite && Interlocked.Increment(ref writes) == 2)
            {
                held.Release();
                release.Wait(TestContext.Current.CancellationToken);
            }
        };
        var seen = Record(editor.Status);
        Task execution;
        try
        {
            execution = editor.ExecuteCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            await B2Harness.WaitUntilAsync(() => editor.Status.Current.Message == oneOfThree);

            // The operation of the window: the progress of the activity, with Stop - and the Execute button stays.
            Assert.Equal(BatchEditorStep.Executing, editor.Step);
            Assert.True(editor.IsExecuteStep);
            Assert.True(editor.Status.IsWorking);
            Assert.Equal(oneOfThree, editor.Status.Current.Message);
            Assert.Equal(1d / 3, editor.Status.Current.Progress!.Value.Fraction!.Value, 5);
            Assert.Equal("Changes.Status.Executing (1, 3, 0)", editor.Status.Current.Log.ToString());
            Assert.Same(editor.Status.CancelCommand, editor.Status.Current.Action!.Command);
            Assert.False(editor.Status.CloseCommand.CanExecute(null));
        }
        finally
        {
            release.Set();
        }

        await execution;
        harness.Session.BeforeOperation = null;

        // Done: what happened, how many, in which domain, and where the report is.
        Assert.Equal(BatchEditorStep.Results, editor.Step);
        Assert.False(editor.IsExecuteStep);
        var result = editor.Status.Current;
        Assert.Equal(PageStatusKind.Done, result.Kind);
        Assert.Equal(done, result.Message);
        Assert.Equal("Changes.Status.Executed (3)", result.Log.ToString());
        Assert.Null(result.Action);
        Assert.All(seen.Take(seen.Count - 1), s => Assert.Equal(PageStatusKind.Working, s.Kind));
        Assert.Equal(3, editor.Result!.SucceededCount);

        // Closed: the counts of the results are the summary.
        Assert.True(editor.Status.CloseCommand.CanExecute(null));
        editor.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, editor.Status.Current.Kind);
        Assert.Equal(editor.ResultSummary, editor.Status.Current.Message);
    }

    [Fact]
    public async Task StopOfAnExecution_CancelsItsActivity_AndEndsAsAttention_WithRetryFailed()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski], operations: new SetAttributeOperation(Attr.Title, "Koordynator V2b"));
        await harness.Settings.UpdateAsync(s => s.BatchMaxParallelism = 1, TestContext.Current.CancellationToken);
        await editor.PreviewCommand.ExecuteAsync(null);
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.IsWrite)
            {
                held.Release();
                release.Wait(TestContext.Current.CancellationToken);
            }
        };
        List<(PageStatusKind Kind, string Message)> seen;
        Task execution;
        try
        {
            execution = editor.ExecuteCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            var activity = editor.CurrentActivity!;
            Assert.True(activity.IsRunning);

            // Stop in the line (the Cancel of the Execute button is the same command): "Stopping…" at once, and the
            // activity of this execution is the one that is cancelled.
            seen = Record(editor.Status);
            var stop = editor.Status.Current.Action!;
            Assert.Equal("Stop", stop.Label);
            stop.Command.Execute(stop.Parameter);
            Assert.True(editor.Status.IsCancelling);
            Assert.True(activity.IsCancelling);
            Assert.False(editor.Status.CancelCommand.CanExecute(null));
        }
        finally
        {
            release.Set();
        }

        await execution;
        harness.Session.BeforeOperation = null;

        // The step that was on its way to the domain controller completed; the rest was not executed.
        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        Assert.Equal(BatchEditorStep.Results, editor.Step);
        Assert.True(editor.Result!.WasCancelled);
        var stopped = editor.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(
            "Executing “Batch edit (2 objects)” in contoso.test was stopped: 1 object changed, failed: 0, the rest was not executed. "
            + "Press “Retry failed” to plan the objects that were not changed again.",
            stopped.Message);
        Assert.Equal("Changes.Status.Stopped (1, 0)", stopped.Log.ToString());
        var retry = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal("Retry failed", retry.Label);
        Assert.Same(editor.RetryFailedCommand, retry.Command);

        // The action plans what was not executed: the line is about that check, then about its preview.
        await editor.RetryFailedCommand.ExecuteAsync(null);
        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Equal(1, editor.ObjectCount);
        Assert.Equal(editor.PreviewSummary, editor.Status.Current.Message);
    }

    [Fact]
    public async Task AnExecutionCancelledInTheListOfBackgroundOperations_MakesTheLineSayStopping()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski], operations: new SetAttributeOperation(Attr.Title, "Koordynator V2b"));
        await editor.PreviewCommand.ExecuteAsync(null);
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.IsWrite)
            {
                held.Release();
                release.Wait(TestContext.Current.CancellationToken);
            }
        };
        Task execution;
        try
        {
            execution = editor.ExecuteCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));

            // The window's own command of before - and the list of background operations - cancel the activity.
            editor.CancelExecutionCommand.Execute(null);
            Assert.True(editor.CurrentActivity!.IsCancelling);
            Assert.True(editor.Status.IsCancelling);
            Assert.Equal("Stopping…", editor.Status.Current.Message);
        }
        finally
        {
            release.Set();
        }

        await execution;
        harness.Session.BeforeOperation = null;
        Assert.Equal(BatchEditorStep.Results, editor.Step);
        Assert.False(editor.Status.IsWorking);
    }

    [Fact]
    public async Task AnExecutionThatFailsInPart_IsAttention_AndOneThatChangesNothing_IsAnError()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski], operations: new SetAttributeOperation(Attr.Title, "Koordynator V2b"));
        await editor.PreviewCommand.ExecuteAsync(null);
        harness.Session.FailNext(FakeOperationKind.Modify, harness.Company.LukaszWisniewski, DirectoryErrorKind.AccessDenied);

        await editor.ExecuteCommand.ExecuteAsync(null);

        // One of two: what was not changed, where the reasons are, and how to go on.
        var partly = editor.Status.Current;
        Assert.Equal(PageStatusKind.Attention, partly.Kind);
        Assert.Equal(
            "“Batch edit (2 objects)” was executed only in part in contoso.test: 1 object changed, failed: 1. "
            + "The reasons are in the list of results. Press “Retry failed” to plan the objects that were not changed again.",
            partly.Message);
        Assert.Equal("Changes.Status.ExecutedPartly (1, 1)", partly.Log.ToString());
        Assert.Same(editor.RetryFailedCommand, partly.Action!.Command);
        Assert.Equal(1, editor.Result!.FailedCount);

        // The retry fails as well: nothing was changed - an error.
        await editor.RetryFailedCommand.ExecuteAsync(null);
        harness.Session.FailNext(FakeOperationKind.Modify, harness.Company.LukaszWisniewski, DirectoryErrorKind.AccessDenied);
        await editor.ExecuteCommand.ExecuteAsync(null);
        var failed = editor.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal(
            "Could not execute “Batch edit (2 objects)” in contoso.test: nothing was changed, failed: 1. "
            + "The reasons are in the list of results. Press “Retry failed” to plan the objects that were not changed again.",
            failed.Message);
        Assert.Equal("Changes.Status.ExecuteFailed (0, 1)", failed.Log.ToString());
        Assert.True(failed.CanClose);
    }

    [Fact]
    public async Task AnUndo_IsExecutedLikeAnyPlan_AndItsResultOffersNoRetry()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski], operations: new SetAttributeOperation(Attr.Title, "Koordynator V2b"));
        await editor.PreviewCommand.ExecuteAsync(null);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, editor.Status.Current.Kind);

        // Undo shows its plan: the line is about that plan now, not about the execution before.
        editor.UndoCommand.Execute(null);
        Assert.True(editor.IsRollbackPreview);
        Assert.Equal(PageStatusKind.Idle, editor.Status.Current.Kind);
        Assert.Equal(editor.PreviewSummary, editor.Status.Current.Message);

        harness.Session.FailNext(FakeOperationKind.Modify, harness.Company.JanKowalski, DirectoryErrorKind.AccessDenied);
        await editor.ExecuteCommand.ExecuteAsync(null);

        var partly = editor.Status.Current;
        Assert.Equal(PageStatusKind.Attention, partly.Kind);
        Assert.EndsWith(": 1 object changed, failed: 1. The reasons are in the list of results.", partly.Message, StringComparison.Ordinal);
        Assert.Null(partly.Action);
        Assert.False(editor.RetryFailedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Back_TakesWhatTheLineSaidAboutTheStepThatIsLeft()
    {
        var (editor, _) = await OpenAsync(c => [c.JanKowalski], operations: new SetAttributeOperation(Attr.Title, "Koordynator V2b"));
        await editor.PreviewCommand.ExecuteAsync(null);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, editor.Status.Current.Kind);

        editor.BackToEditCommand.Execute(null);

        Assert.Equal(BatchEditorStep.Edit, editor.Step);
        Assert.Same(editor.SourceStatus, editor.Status.Front);
        Assert.Equal(PageStatusKind.Idle, editor.Status.Current.Kind);
        Assert.Equal("1 object · User: 1", editor.Status.Current.Message);
    }

    // ---- The session of the window, closing ------------------------------------------------------------------------

    [Fact]
    public async Task WhenTheSessionOfTheWindowEnds_TheLineSaysSoForGood_AndAnExecutionThatCannotStartIsAnError()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski], operations: new SetAttributeOperation(Attr.Title, "Koordynator V2b"));
        await editor.PreviewCommand.ExecuteAsync(null);

        await harness.Sessions.DisconnectAsync();

        // An error that cannot be closed - the same sentence in every window.
        var ended = editor.Status.Current;
        Assert.True(editor.IsSessionEnded);
        Assert.Equal(PageStatusKind.Error, ended.Kind);
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], ended.Message);
        Assert.Equal("WindowSession.Ended", ended.Log.ToString());
        Assert.False(ended.CanClose);
        Assert.Same(editor.Status.Lasting, ended);
        Assert.Equal(ended.Message, editor.Error);
        Assert.False(editor.ExecuteCommand.CanExecute(null));
        Assert.False(editor.ReloadTargetsCommand.CanExecute(null));

        // The command run all the same (a key press that was on its way): nothing is executed, the line says why -
        // and when that is closed, the state of the window is back.
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        var failed = editor.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith("Could not execute “Batch edit (1 objects)” in contoso.test: ", failed.Message, StringComparison.Ordinal);
        Assert.Equal("Changes.Status.ExecuteFailed [SessionEndedException]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Empty(harness.Session.Writes);
        editor.Status.CloseCommand.Execute(null);
        Assert.Same(editor.Status.Lasting, editor.Status.Current);

        // In another language: the state says its sentence again.
        harness.Localizer.SetCulture(Polish);
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], editor.Status.Current.Message);
    }

    [Fact]
    public async Task WhenTheSessionEndsDuringACheck_TheCheckEndsWithoutAMessage()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski], operations: new DeleteOperation());
        harness.Session.Latency = SlowDomainController;
        List<(PageStatusKind Kind, string Message)> seen;
        try
        {
            var preview = editor.PreviewCommand.ExecuteAsync(null);
            Assert.True(editor.Status.IsWorking);
            seen = Record(editor.Status);

            await harness.Sessions.DisconnectAsync();
            await preview.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        finally
        {
            harness.Session.Latency = TimeSpan.Zero;
        }

        Assert.DoesNotContain(seen, s => s.Kind == PageStatusKind.Attention || s.Message == "Stopping…");
        Assert.False(editor.IsBusy);
        Assert.Equal(PageStatusKind.Error, editor.Status.Current.Kind);
        Assert.Same(editor.Status.Lasting, editor.Status.Current);
    }

    [Fact]
    public async Task ClosingTheWindow_EndsWhatItSays_ButNotTheExecution()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski], operations: new SetAttributeOperation(Attr.Title, "Koordynator V2b"));
        await editor.PreviewCommand.ExecuteAsync(null);
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.IsWrite)
            {
                held.Release();
                release.Wait(TestContext.Current.CancellationToken);
            }
        };
        Task execution;
        ActivityViewModel activity;
        try
        {
            execution = editor.ExecuteCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            activity = editor.CurrentActivity!;

            editor.Dispose();

            // The window no longer says "working"; the change keeps running in the background.
            Assert.False(editor.Status.IsWorking);
            Assert.Null(editor.Status.Front);
            Assert.True(activity.IsRunning);
            Assert.False(activity.IsCancelling);
        }
        finally
        {
            release.Set();
        }

        await execution;
        harness.Session.BeforeOperation = null;
        Assert.False(activity.WasCancelled);
        Assert.Equal("Koordynator V2b", harness.Value(harness.Company.JanKowalski, Attr.Title));
        Assert.Null(editor.Result);
    }

    // ---- The source of the first step: the CSV file ----------------------------------------------------------------

    [Fact]
    public async Task TheCsvFile_IsReadAsAnOperationOfTheImport_AndNamedInTheSummary()
    {
        var harness = await B2Harness.CreateAsync();
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([]));
        var csv = editor.CsvImport!;
        await editor.InitializeAsync();

        // Nothing loaded: nothing to say (the hint is next to the Browse button).
        Assert.Same(csv.Status, editor.SourceStatus);
        Assert.Same(csv.Status, editor.Status.Front);
        Assert.Equal(string.Empty, editor.Status.Current.Message);

        var path = Path.Combine(Path.GetTempPath(), $"admng-status-{Guid.NewGuid():N}.csv");
        var name = Path.GetFileName(path);
        var seen = Record(editor.Status);
        try
        {
            await File.WriteAllTextAsync(
                path, "sAMAccountName;title\r\njan.kowalski;Kierownik\r\nlukasz.wisniewski;Programista\r\n", new UTF8Encoding(true), TestContext.Current.CancellationToken);
            await csv.LoadFileAsync(path);
        }
        finally
        {
            File.Delete(path);
        }

        // Working with the name of the file, then the file is the content: its name and size are the summary.
        Assert.Equal((PageStatusKind.Working, $"Reading “{name}”…"), seen[0]);
        Assert.Equal((PageStatusKind.Idle, $"{name} · 2 rows, delimiter: ;"), seen[^1]);
        Assert.DoesNotContain(seen, s => s.Kind is PageStatusKind.Done or PageStatusKind.Attention or PageStatusKind.Error);
        Assert.False(csv.IsLoading);
        Assert.False(csv.Status.IsWorking);
        Assert.Null(csv.Error);

        // The same file again, gone meanwhile: what was tried, why, what to do - and the loaded file stays.
        await csv.LoadFileAsync(path);
        var failed = editor.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith($"Could not read “{name}”: ", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Choose another file. (FileNotFoundException)", failed.Message, StringComparison.Ordinal);
        Assert.Equal("Csv.Status.ReadFailed [FileNotFoundException]", failed.Log.ToString());
        Assert.DoesNotContain(name, failed.ToString(), StringComparison.Ordinal);
        Assert.True(failed.CanClose);
        Assert.NotNull(csv.Error);
        Assert.Equal(2, csv.RowCount);
        Assert.False(csv.IsLoading);
        editor.Status.CloseCommand.Execute(null);
        Assert.Equal($"{name} · 2 rows, delimiter: ;", editor.Status.Current.Message);
    }

    [Fact]
    public async Task AFileThatCannotBeUsed_IsAnError_AndAFileWithoutRows_IsANoteOfTheMapping()
    {
        var harness = await B2Harness.CreateAsync(Polish);
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([]));
        var csv = editor.CsvImport!;

        // No header row: nothing is loaded.
        csv.Load(new CsvTable([], [], ';'));
        var unusable = editor.Status.Current;
        Assert.Equal(PageStatusKind.Error, unusable.Kind);
        Assert.Equal("Pliku nie wczytano: Plik nie ma wiersza nagłówka. Wybierz inny plik.", unusable.Message);
        Assert.Equal("Csv.Status.NotLoaded", unusable.Log.ToString());
        Assert.False(csv.IsLoaded);

        // A header without data rows is loaded - the line names it; why it cannot be planned is the note of the mapping.
        csv.Load(new CsvTable(["login", "title"], [], ';'));
        Assert.Equal(PageStatusKind.Idle, editor.Status.Current.Kind);
        Assert.Equal("Wiersze: 0, separator: ;", editor.Status.Current.Message);
        Assert.Equal("Plik nie zawiera wierszy danych.", csv.MappingError);
        Assert.Equal("Plik nie zawiera wierszy danych.", csv.Error);
        Assert.False(editor.PreviewCommand.CanExecute(null));

        // A usable file takes the place of both.
        csv.Load(new CsvTable(["sAMAccountName", "title"], [["jan.kowalski", "Kierownik"]], ';'));
        Assert.Equal("Wiersze: 1, separator: ;", editor.Status.Current.Message);
        Assert.Null(csv.Error);
        Assert.True(editor.PreviewCommand.CanExecute(null));

        // Checking the rows is the operation of the window; in the preview the file is no longer in front.
        await editor.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Null(editor.Status.Front);
        Assert.Equal(editor.PreviewSummary, editor.Status.Current.Message);
    }

    // ---- The diagnostic log ------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLogCopies_CarryKeysAndNumbers_NeverATitleANameOrAServerText()
    {
        var harness = await B2Harness.CreateAsync();
        var logger = new ListLogger<PageStatusHolder>();
        var lists = new ObjectListServices(harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, logger);
        var services = new DialogServices(
            harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, harness.Dialogs, harness.Files,
            harness.Clipboard, harness.Notifications, harness.Coordinator, harness.Activities, lists, harness.Regional);
        using var editor = new BatchEditorViewModel(
            services,
            harness.Engines,
            new BatchEditorRequest([harness.Company.JanKowalski, harness.Company.LukaszWisniewski])
            {
                Operations = [new SetAttributeOperation(Attr.Title, "Kierownik zespołu")],
                Title = "Awans: Jan Kowalski i Łukasz Wiśniewski",
            });
        await editor.InitializeAsync();
        await editor.PreviewCommand.ExecuteAsync(null);
        harness.Session.FailNext(FakeOperationKind.Modify, harness.Company.LukaszWisniewski, DirectoryErrorKind.AccessDenied);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Contains("Awans: Jan Kowalski i Łukasz Wiśniewski", editor.Status.Current.Message, StringComparison.Ordinal);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("Batch.Status.ReadingTargets", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Batch.Status.Checking", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Changes.Status.Executing", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Changes.Status.ExecutedPartly (1, 1)", StringComparison.Ordinal));
        Assert.DoesNotContain("Awans", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Kowalski", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Wiśniewski", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Kierownik", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(BatchEditorViewModel Editor, B2Harness Harness)> OpenAsync(
        Func<SampleCompany, IReadOnlyList<string>> targets, CultureInfo? culture = null, params BatchOperation[] operations)
    {
        var harness = await B2Harness.CreateAsync(culture);
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest(targets(harness.Company)) { Operations = operations });
        await editor.InitializeAsync();
        return (editor, harness);
    }

    private static List<(PageStatusKind Kind, string Message)> Record(PageStatusHolder status)
    {
        var seen = new List<(PageStatusKind Kind, string Message)>();
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                lock (seen)
                {
                    seen.Add((status.Current.Kind, status.Current.Message));
                }
            }
        };
        status.PropertyChanged += handler;
        return seen;
    }
}
