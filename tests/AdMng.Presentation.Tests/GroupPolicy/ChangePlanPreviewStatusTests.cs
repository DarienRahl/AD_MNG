using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// The status line of the change plan preview (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-1 as built"):
/// at rest the summary of the plan shown, the execution as the operation of the window with Stop, its result with the
/// domain - and Undo as the action where something was made - and that the session of the window ended. "The undo
/// plan is shown" is a note of the plan, not a status. How plans are executed is covered, unchanged, by
/// <see cref="ChangePlanPreviewTests"/>.
/// </summary>
public sealed class ChangePlanPreviewStatusTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task TheLine_ShowsTheSummaryOfThePlan()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        var plan = await CopyPlanAsync(h);
        using var preview = new ChangePlanPreviewViewModel(h.Ui, h.B2.Engines, new ChangePlanPreviewRequest(plan, "Copy of the password policy"));

        Assert.IsAssignableFrom<IHasPageStatus>(preview);
        Assert.Equal(PageStatusKind.Idle, preview.Status.Current.Kind);
        Assert.Equal(preview.Summary, preview.Status.Current.Message);
        Assert.Contains(h.Session.Info.Server, preview.Status.Current.Message, StringComparison.Ordinal);
        Assert.False(preview.Status.IsWorking);
        Assert.Null(preview.Status.Lasting);
        Assert.False(preview.Status.CloseCommand.CanExecute(null));
        Assert.True(preview.IsExecuteStep);
        Assert.False(preview.IsRollbackNoteShown);

        // The summary follows the language.
        h.B2.Localizer.SetCulture(Polish);
        Assert.Equal(preview.Summary, preview.Status.Current.Message);
        Assert.StartsWith("Obiektów: ", preview.Status.Current.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-US", "Executing “Kopia” in contoso.test: 0 of {0}…", "“Kopia” was executed in contoso.test: {0} changed. The report is in the list of background operations.")]
    [InlineData("pl-PL", "Wykonywanie „Kopia” w domenie contoso.test: 0 z {0}…", "Wykonano „Kopia” w domenie contoso.test: zmieniono {0}. Raport jest na liście operacji w tle.")]
    public async Task Execute_IsTheOperationOfTheWindow_AndTheResultNamesTheDomain_ThenUndoTheSame(string culture, string working, string done)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var plan = await CopyPlanAsync(h);
        using var preview = new ChangePlanPreviewViewModel(h.Ui, h.B2.Engines, new ChangePlanPreviewRequest(plan, "Kopia"));
        var seen = Record(preview.Status);

        await preview.ExecuteCommand.ExecuteAsync(null);

        // Working from the first moment (the line never shows the summary in between), then what happened and where.
        var objects = plan.Objects.Count;
        Assert.Equal((PageStatusKind.Working, string.Format(CultureInfo.InvariantCulture, working, objects)), seen[0]);
        Assert.All(seen.Take(seen.Count - 1), s => Assert.Equal(PageStatusKind.Working, s.Kind));
        Assert.True(preview.IsResultsStep);
        Assert.False(preview.IsExecuteStep);
        Assert.Null(preview.CurrentActivity);
        var result = preview.Status.Current;
        var changed = preview.Result!.SucceededCount;
        Assert.True(changed > 0);
        Assert.Equal(PageStatusKind.Done, result.Kind);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, done, PageStatusText.Objects(h.B2.Localizer, changed)), result.Message);
        Assert.Equal($"Changes.Status.Executed ({changed})", result.Log.ToString());
        Assert.Null(result.Action);
        Assert.Equal(seen[^1], (result.Kind, result.Message));

        // Closed: the summary of the plan again.
        preview.Status.CloseCommand.Execute(null);
        Assert.Equal(preview.Summary, preview.Status.Current.Message);

        // Undo shows its plan: the line is about that plan (its summary), and that it is the undo plan is a note of the
        // plan while it waits to be executed.
        preview.UndoCommand.Execute(null);
        Assert.True(preview.IsRollbackPreview);
        Assert.True(preview.IsRollbackNoteShown);
        Assert.Equal(PageStatusKind.Idle, preview.Status.Current.Kind);
        Assert.Equal(preview.Summary, preview.Status.Current.Message);

        await preview.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, preview.Status.Current.Kind);
        Assert.Contains("contoso.test", preview.Status.Current.Message, StringComparison.Ordinal);
        Assert.Contains(preview.ChangeTitle, preview.Status.Current.Message, StringComparison.Ordinal);
        Assert.False(preview.IsRollbackNoteShown);
    }

    [Fact]
    public async Task UndoShownRightAfterTheResult_TakesTheResultOffTheLine()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        var plan = await CopyPlanAsync(h);
        using var preview = new ChangePlanPreviewViewModel(h.Ui, h.B2.Engines, new ChangePlanPreviewRequest(plan, "Kopia"));
        await preview.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, preview.Status.Current.Kind);

        preview.UndoCommand.Execute(null);

        Assert.Equal(PageStatusKind.Idle, preview.Status.Current.Kind);
        Assert.Equal(preview.Summary, preview.Status.Current.Message);
        Assert.Equal(h.B2.Localizer[L.PlanPreview.RollbackShown], preview.ResultText);
    }

    [Fact]
    public async Task AnExecutionThatFails_SaysWhatWasNotMade_AndOffersUndoWhereSomethingWasMade()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        var plan = await CopyPlanAsync(h);
        h.Sysvol.Fail("Replace", SysvolWriteError.AccessDenied);
        using var preview = new ChangePlanPreviewViewModel(h.Ui, h.B2.Engines, new ChangePlanPreviewRequest(plan, "Kopia"));

        await preview.ExecuteCommand.ExecuteAsync(null);

        Assert.True(preview.ResultHasFailures);
        var result = preview.Result!;
        var status = preview.Status.Current;
        Assert.True(result.FailedCount > 0);

        // Attention when something was made, an error when nothing was - with the numbers of this result.
        Assert.Equal(result.SucceededCount > 0 ? PageStatusKind.Attention : PageStatusKind.Error, status.Kind);
        Assert.Contains("“Kopia”", status.Message, StringComparison.Ordinal);
        Assert.Contains(" in contoso.test: ", status.Message, StringComparison.Ordinal);
        Assert.Contains($"failed: {result.FailedCount}", status.Message, StringComparison.Ordinal);
        Assert.Contains("The reasons are in the list of results.", status.Message, StringComparison.Ordinal);
        Assert.Equal(
            result.SucceededCount > 0
                ? $"Changes.Status.ExecutedPartly ({result.SucceededCount}, {result.FailedCount})"
                : $"Changes.Status.ExecuteFailed (0, {result.FailedCount})",
            status.Log.ToString());
        Assert.True(status.CanClose);

        // Undo is the action of the line exactly when there is something to undo.
        if (preview.UndoCommand.CanExecute(null))
        {
            Assert.EndsWith(" Press Undo to revert what was made.", status.Message, StringComparison.Ordinal);
            var undo = Assert.IsType<PageStatusAction>(status.Action);
            Assert.Equal("Undo", undo.Label);
            Assert.Same(preview.UndoCommand, undo.Command);
        }
        else
        {
            Assert.Null(status.Action);
            Assert.DoesNotContain("Undo", status.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task StopOfAnExecution_SaysStoppingAtOnce_CancelsTheActivity_AndEndsAsAttention()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        var plan = await CopyPlanAsync(h);
        using var preview = new ChangePlanPreviewViewModel(h.Ui, h.B2.Engines, new ChangePlanPreviewRequest(plan, "Kopia"));
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = 1;
        h.Session.BeforeOperation = operation =>
        {
            // The first write of the plan waits for the test.
            if (operation.IsWrite && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Release();
                release.Wait(TestContext.Current.CancellationToken);
            }
        };
        List<(PageStatusKind Kind, string Message)> seen;
        Task execution;
        try
        {
            execution = preview.ExecuteCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            var activity = preview.CurrentActivity!;

            // While it runs: the Execute button stays (it is the Cancel now) and the window cannot be closed.
            Assert.True(preview.IsExecutingStep);
            Assert.True(preview.IsExecuteStep);
            Assert.True(preview.Status.IsWorking);
            Assert.False(preview.CloseCommand.CanExecute(null));
            Assert.False(await preview.CanCloseAsync());
            var stop = Assert.IsType<PageStatusAction>(preview.Status.Current.Action);
            Assert.Equal("Stop", stop.Label);
            Assert.Same(preview.Status.CancelCommand, stop.Command);

            seen = Record(preview.Status);
            stop.Command.Execute(stop.Parameter);
            Assert.True(preview.Status.IsCancelling);
            Assert.True(activity.IsCancelling);
        }
        finally
        {
            release.Set();
        }

        await execution;
        h.Session.BeforeOperation = null;

        Assert.Equal((PageStatusKind.Working, "Stopping…"), seen[0]);
        Assert.True(preview.IsResultsStep);
        Assert.True(preview.Result!.WasCancelled);
        var stopped = preview.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.StartsWith("Executing “Kopia” in contoso.test was stopped: ", stopped.Message, StringComparison.Ordinal);
        Assert.Contains("the rest was not executed.", stopped.Message, StringComparison.Ordinal);
        Assert.Equal($"Changes.Status.Stopped ({preview.Result.SucceededCount}, {preview.Result.FailedCount})", stopped.Log.ToString());
        Assert.True(preview.CloseCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARefusedConfirmation_SaysNothing()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var plan = await h.Planner.PlanDeleteAsync(h.Session, h.Sample.SitePrinters, CancellationToken.None);
        using var preview = new ChangePlanPreviewViewModel(h.Ui, h.B2.Engines, new ChangePlanPreviewRequest(plan, "Usunięcie")
        {
            ConfirmationText = "Lokacja Warszawa – Drukarki",
            ConfirmationMessage = "Usunąć z domeny contoso.test?",
        });
        var seen = Record(preview.Status);
        h.B2.Dialogs.TypedConfirmAnswers.Enqueue(false);

        await preview.ExecuteCommand.ExecuteAsync(null);

        Assert.Empty(seen);
        Assert.Equal(preview.Summary, preview.Status.Current.Message);
        Assert.True(preview.IsPreviewStep);
    }

    [Fact]
    public async Task WhenTheSessionOfTheWindowEnds_TheLineSaysSoForGood_AndAnExecutionThatCannotStartIsAnError()
    {
        var h = await GroupPolicyHarness.CreateAsync(English);
        var plan = await CopyPlanAsync(h);
        using var preview = new ChangePlanPreviewViewModel(h.Ui, h.B2.Engines, new ChangePlanPreviewRequest(plan, "Kopia – „Zasady haseł”"));

        await h.B2.Sessions.DisconnectAsync();

        var ended = preview.Status.Current;
        Assert.True(preview.IsSessionEnded);
        Assert.Equal(PageStatusKind.Error, ended.Kind);
        Assert.Equal(h.B2.Localizer[L.WindowSession.Ended], ended.Message);
        Assert.Equal("WindowSession.Ended", ended.Log.ToString());
        Assert.False(ended.CanClose);
        Assert.Same(preview.Status.Lasting, ended);
        Assert.False(preview.ExecuteCommand.CanExecute(null));
        Assert.Equal(h.B2.Localizer[L.PlanPreview.SessionEnded], preview.Error);

        // The command run all the same: nothing is executed, the line says why, and its log copy names nothing.
        await preview.ExecuteCommand.ExecuteAsync(null);
        Assert.True(preview.IsPreviewStep);
        var failed = preview.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith("Could not execute “Kopia – „Zasady haseł”” in contoso.test: ", failed.Message, StringComparison.Ordinal);
        Assert.Equal("Changes.Status.ExecuteFailed [SessionEndedException]", failed.Log.ToString());
        Assert.DoesNotContain("Zasady", failed.ToString(), StringComparison.Ordinal);
        Assert.True(failed.CanClose);
        preview.Status.CloseCommand.Execute(null);
        Assert.Same(preview.Status.Lasting, preview.Status.Current);
    }

    private static async Task<ChangePlan> CopyPlanAsync(GroupPolicyHarness h)
    {
        await using var sysvol = await h.Sysvol.OpenForWriteAsync(h.Session, CancellationToken.None);
        return await h.Planner.PlanCopyAsync(h.Session, sysvol, h.Sample.WorkstationPasswords, "Kopia – Zasady haseł", copyPermissions: false, CancellationToken.None);
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
