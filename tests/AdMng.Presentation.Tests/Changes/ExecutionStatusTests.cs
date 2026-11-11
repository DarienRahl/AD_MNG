using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Tests.Dialogs;

namespace AdMng.Presentation.Tests.Changes;

/// <summary>
/// <see cref="ExecutionStatus"/> (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-1 as built"): the execution
/// of a change plan as the operation of a window's status line - working from the first moment, the progress of the
/// activity (of several activities one after another), Stop in both directions, and an operation that merely ends
/// never cancels the activity. The results are tested through the windows that use it
/// (<c>BatchEditorStatusTests</c>, <c>ChangePlanPreviewStatusTests</c>).
/// </summary>
public class ExecutionStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task TheExecution_IsWorkingFromTheFirstMoment_AndFollowsTheProgressOfItsActivity()
    {
        var harness = await B2Harness.CreateAsync();
        var status = new PageStatusHolder(harness.Localizer, harness.Dispatcher);
        status.SetIdle("3 objects");
        var seen = Record(status);

        using var execution = ExecutionStatus.Begin(status, harness.Localizer, "Disable accounts", "contoso.test", 3);

        // Before the first object is done: working, how far is not known (the ring turns), Stop is offered.
        Assert.True(status.IsWorking);
        Assert.Equal("Executing “Disable accounts” in contoso.test: 0 of 3…", status.Current.Message);
        Assert.True(status.Current.Progress!.Value.IsIndeterminate);
        Assert.Equal("Stop", status.Current.Action!.Label);
        Assert.Same(status.CancelCommand, status.Current.Action.Command);
        Assert.Equal("Changes.Status.Executing (0, 3, 0)", status.Current.Log.ToString());

        var work = new Work();
        var (activity, run) = harness.Activities.Start("Disable accounts", 3, work.RunAsync);
        await work.Started;
        execution.Follow(activity);

        // The part done, then with the failures so far - one message all along (said and logged once).
        work.Report(1, 3, 0);
        Assert.Equal("Executing “Disable accounts” in contoso.test: 1 of 3…", status.Current.Message);
        Assert.Equal(1d / 3, status.Current.Progress!.Value.Fraction!.Value, 5);
        work.Report(2, 3, 1);
        Assert.Equal("Executing “Disable accounts” in contoso.test: 2 of 3, failed: 1…", status.Current.Message);
        Assert.Equal(2d / 3, status.Current.Progress!.Value.Fraction!.Value, 5);
        Assert.Equal("Changes.Status.Executing (2, 3, 1)", status.Current.Log.ToString());
        Assert.All(seen, s => Assert.Equal(PageStatusKind.Working, s.Kind));
        Assert.True(status.Current.IsSameMessageAs(PageStatus.Working("x", new PageStatusLog("Changes.Status.Executing"))));

        // The activity ends: its last counters are not a progress report - the result says what happened.
        var message = status.Current.Message;
        work.End(Result());
        await run;
        Assert.Equal(message, status.Current.Message);
        Assert.True(status.IsWorking);
    }

    [Fact]
    public async Task SeveralActivitiesOneAfterAnother_AddUp()
    {
        var harness = await B2Harness.CreateAsync(Polish);
        var status = new PageStatusHolder(harness.Localizer, harness.Dispatcher);
        using var execution = ExecutionStatus.Begin(status, harness.Localizer, "Zakładanie kont", "contoso.test", 4);
        Assert.Equal("Wykonywanie „Zakładanie kont” w domenie contoso.test: 0 z 4…", status.Current.Message);

        // One object first (the wizard's canary) …
        var first = new Work();
        var (canary, firstRun) = harness.Activities.Start("Zakładanie kont", 1, first.RunAsync);
        await first.Started;
        execution.Follow(canary);
        first.Report(1, 1, 0);
        Assert.Equal("Wykonywanie „Zakładanie kont” w domenie contoso.test: 1 z 4…", status.Current.Message);
        first.End(Result());
        await firstRun;

        // … then the rest: its numbers continue those of the first.
        var second = new Work();
        var (rest, secondRun) = harness.Activities.Start("Zakładanie kont", 3, second.RunAsync);
        await second.Started;
        execution.Follow(rest);
        Assert.Equal("Wykonywanie „Zakładanie kont” w domenie contoso.test: 1 z 4…", status.Current.Message);
        second.Report(2, 3, 1);
        Assert.Equal("Wykonywanie „Zakładanie kont” w domenie contoso.test: 3 z 4, niepowodzenia: 1…", status.Current.Message);
        Assert.Equal(0.75, status.Current.Progress!.Value.Fraction!.Value, 5);
        Assert.Equal("Changes.Status.Executing (3, 4, 1)", status.Current.Log.ToString());

        // A report of the activity that is over changes nothing any more.
        first.Report(1, 1, 1);
        Assert.Equal("Wykonywanie „Zakładanie kont” w domenie contoso.test: 3 z 4, niepowodzenia: 1…", status.Current.Message);
        second.End(Result());
        await secondRun;
    }

    [Fact]
    public async Task Stop_InTheStatusLine_CancelsTheActivity_AndSaysStoppingAtOnce()
    {
        var harness = await B2Harness.CreateAsync();
        var status = new PageStatusHolder(harness.Localizer, harness.Dispatcher);
        using var execution = ExecutionStatus.Begin(status, harness.Localizer, "Disable accounts", "contoso.test", 3);
        var work = new Work();
        var (activity, run) = harness.Activities.Start("Disable accounts", 3, work.RunAsync);
        await work.Started;
        execution.Follow(activity);
        var seen = Record(status);

        var stop = status.Current.Action!;
        stop.Command.Execute(stop.Parameter);

        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        Assert.True(status.IsCancelling);
        Assert.True(activity.IsCancelling);
        Assert.True(work.Token.IsCancellationRequested);

        // A progress report that arrives now does not take "Stopping…" off the line.
        work.Report(1, 3, 0);
        Assert.Equal("Stopping…", status.Current.Message);
        work.End(Result(cancelled: true));
        await run;
    }

    [Fact]
    public async Task AnActivityCancelledInTheListOfBackgroundOperations_MakesTheWindowSayStopping()
    {
        var harness = await B2Harness.CreateAsync();
        var status = new PageStatusHolder(harness.Localizer, harness.Dispatcher);
        using var execution = ExecutionStatus.Begin(status, harness.Localizer, "Disable accounts", "contoso.test", 3);
        var work = new Work();
        var (activity, run) = harness.Activities.Start("Disable accounts", 3, work.RunAsync);
        await work.Started;
        execution.Follow(activity);

        activity.CancelCommand.Execute(null);

        Assert.True(status.IsCancelling);
        Assert.Equal("Stopping…", status.Current.Message);
        Assert.False(status.CancelCommand.CanExecute(null));
        work.End(Result(cancelled: true));
        await run;
    }

    [Fact]
    public async Task StopPressedBeforeTheActivityIsKnown_ReachesItWhenItStarts()
    {
        var harness = await B2Harness.CreateAsync();
        var status = new PageStatusHolder(harness.Localizer, harness.Dispatcher);
        using var execution = ExecutionStatus.Begin(status, harness.Localizer, "Disable accounts", "contoso.test", 3);

        status.CancelCommand.Execute(null);
        Assert.Equal("Stopping…", status.Current.Message);

        var work = new Work();
        var (activity, run) = harness.Activities.Start("Disable accounts", 3, work.RunAsync);
        await work.Started;
        execution.Follow(activity);

        Assert.True(activity.IsCancelling);
        work.End(Result(cancelled: true));
        await run;
    }

    [Fact]
    public async Task AnOperationThatMerelyEnds_DoesNotCancelTheActivity()
    {
        var harness = await B2Harness.CreateAsync();
        var status = new PageStatusHolder(harness.Localizer, harness.Dispatcher);
        status.SetIdle("3 objects");
        var work = new Work();
        var (activity, run) = harness.Activities.Start("Disable accounts", 3, work.RunAsync);
        await work.Started;

        // Disposed without a result (the window was closed): the summary again, and the execution keeps running.
        var execution = ExecutionStatus.Begin(status, harness.Localizer, "Disable accounts", "contoso.test", 3);
        execution.Follow(activity);
        execution.Dispose();
        Assert.False(status.IsWorking);
        Assert.Equal("3 objects", status.Current.Message);
        Assert.False(activity.IsCancelling);
        Assert.False(work.Token.IsCancellationRequested);

        // Taken off the line by something else the window says: the same.
        var second = ExecutionStatus.Begin(status, harness.Localizer, "Disable accounts", "contoso.test", 3);
        second.Follow(activity);
        status.Show(PageStatus.Attention("Something else.", new PageStatusLog("Test.Other")));
        Assert.False(activity.IsCancelling);
        Assert.False(work.Token.IsCancellationRequested);

        // What the activity reports afterwards is nobody's business here.
        work.Report(2, 3, 0);
        Assert.Equal("Something else.", status.Current.Message);
        second.Dispose();
        Assert.True(activity.IsRunning);
        work.End(Result());
        await run;
    }

    [Fact]
    public async Task APlanThatCouldNotBeExecuted_IsAnError_WithTheReasonAndTheCode_AndTheLogCarriesNoText()
    {
        var harness = await B2Harness.CreateAsync();
        var status = new PageStatusHolder(harness.Localizer, harness.Dispatcher);
        using var execution = ExecutionStatus.Begin(status, harness.Localizer, "Disable “Łukasz Żółć”", "contoso.test", 1);

        execution.Fail("The session of this window ended.", ResultCode.Of(new InvalidOperationException("CN=Łukasz Żółć")));

        var error = status.Current;
        Assert.Equal(PageStatusKind.Error, error.Kind);
        Assert.Equal("Could not execute “Disable “Łukasz Żółć”” in contoso.test: The session of this window ended. (InvalidOperationException)", error.Message);
        Assert.True(error.CanClose);
        Assert.Null(error.Action);
        Assert.Equal("Changes.Status.ExecuteFailed [InvalidOperationException]", error.Log.ToString());
        Assert.DoesNotContain("Łukasz", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWindowWithoutADomain_SaysTheSameWithoutNamingOne()
    {
        var harness = await B2Harness.CreateAsync(Polish);
        var status = new PageStatusHolder(harness.Localizer, harness.Dispatcher);
        using var execution = ExecutionStatus.Begin(status, harness.Localizer, "Zakładanie kont", string.Empty, 4);

        Assert.Equal("Wykonywanie „Zakładanie kont”: 0 z 4…", status.Current.Message);
        execution.Fail("Brak połączenia z katalogiem.");
        Assert.Equal("Nie udało się wykonać „Zakładanie kont”: Brak połączenia z katalogiem.", status.Current.Message);
    }

    private static BatchResult Result(bool cancelled = false)
    {
        var at = DateTimeOffset.UnixEpoch;
        return new BatchResult(new ChangePlan { Objects = [] }, [], "dc01.contoso.test", "CONTOSO\\operator", at, at, cancelled);
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

    /// <summary>The work of an activity that the test drives: it reports and ends when the test says so.</summary>
    private sealed class Work
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<BatchResult> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<BatchProgress>? _progress;

        /// <summary>Completes when the activity center handed over the progress sink and the token.</summary>
        public Task Started => _started.Task;

        public CancellationToken Token { get; private set; }

        public Task<BatchResult> RunAsync(IProgress<BatchProgress> progress, CancellationToken token)
        {
            _progress = progress;
            Token = token;
            _started.SetResult();
            return _end.Task;
        }

        public void Report(int completed, int total, int failed) => _progress!.Report(new BatchProgress(completed, total, failed, null));

        public void End(BatchResult result) => _end.SetResult(result);
    }
}
