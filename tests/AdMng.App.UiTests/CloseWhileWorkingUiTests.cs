using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// The close question of a window that works (track V, stage V2b-5b, the owner's decisions 6 and D-A; docs/dev/v-interface-renewal.md
/// "V2b-5b as built" and "D-A repair as built"), through the real windows - their lifecycle, the status line and the answers of a
/// scripted dialog service - over the fake directory: a window closed while it executes asks with three answers; Don't close keeps
/// it, Stop and close stops it and the window closes after the object in progress was finished, Close and continue in the background
/// closes it at once and the connection says how the execution ended; a window that only reads closes at once; a window whose write
/// cannot be stopped says it is still working and stays; the main window asks one question, never offers the background, and waits
/// for what it stopped. A slow domain controller is a call the test holds without blocking a thread and lets go when it has looked;
/// every state that is asserted lasts until the test changes it.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CloseWhileWorkingUiTests
{
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task ClosingAnExecutingWindow_AsksFirst_NoKeepsIt_YesStopsAndClosesAfterTheObjectInProgressWasFinished() => WpfTestHost.RunAsync(async () =>
    {
        var dialogs = new ScriptedDialogService();
        await using var context = await CreateContextAsync(dialogs);
        var company = context.Company;
        var (window, editor) = await OpenEditorAsync(context, new BatchEditorRequest([company.JanKowalski, company.LukaszWisniewski])
        {
            Operations = [new SetAttributeOperation(Attr.Title, "Koordynator V2b")],
        });
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;
        await editor.PreviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var closed = false;
        window.Closed += (_, _) => closed = true;

        // One object after another; the write of the first one waits for the test.
        var first = new Gate();
        session.BeforeOperationAsync = operation => operation.IsWrite ? first.PassAsync() : Task.CompletedTask;
        try
        {
            ControlHost.Press(window.ExecuteButton);
            await UiWait.UntilAsync(
                () => first.Reached.Task.IsCompleted && editor.Step == BatchEditorStep.Executing && editor.Status.IsWorking, "the first object is being written");
            Assert.True(editor.Status.Operation is { Writes: OperationWrites.Changes, CanCancel: true });

            // The operator closes the window during the execution, and says Don't close: the question names what runs and what each
            // answer does - three of them, the execution may go on without the window; the window stays and nothing is stopped.
            dialogs.CloseAnswers.Enqueue(CloseAnswer.KeepOpen);
            window.Close();
            await UiWait.UntilAsync(() => dialogs.CloseQuestions.Count == 1, "the question is asked");
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(
                context.Services.GetRequiredService<ILocalizer>().Format(
                    L.CloseWork.Changes, $"Executing “{editor.ChangeTitle}” in {domain}: 0 of 2…"),
                dialogs.CloseQuestions[0].Message);
            Assert.True(dialogs.CloseQuestions[0].CanContinue);
            Assert.True(window.IsVisible);
            Assert.False(closed);
            Assert.False(editor.Status.IsCancelling);
            Assert.Equal(ActionButtonState.Running, window.ExecuteButton.State);

            // Stop and close: the holder's own Stop - "Stopping…" - and the window waits for the end: the object whose write is on its
            // way is finished first, so it is still open while that write has not gone through.
            dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);
            window.Close();
            await UiWait.UntilAsync(() => dialogs.CloseQuestions.Count == 2 && editor.Status.IsCancelling, "the execution is stopping");
            await UiWait.UntilAsync(() => window.ExecuteButton.State == ActionButtonState.Cancelling, "the Execute button says it is stopping");
            Assert.True(window.IsVisible);
            Assert.False(closed);
            Assert.DoesNotContain(session.Operations, operation => operation.IsWrite);
        }
        finally
        {
            first.Release();
        }

        await UiWait.UntilAsync(() => closed, "the window closed after the execution ended");

        // The account that was on its way was finished; the other one was not started.
        Assert.True(editor.IsDisposed);
        var written = Assert.Single(session.Operations, operation => operation.IsWrite);
        Assert.Equal(company.JanKowalski, written.DistinguishedName);
        Assert.Equal(2, dialogs.CloseQuestions.Count);
        context.AssertHealthy();
    });

    [Fact]
    public Task ContinueInTheBackground_ClosesTheRealWindowAtOnce_TheActivityRowRunsOn_TheConnectionToastsTheOutcome() => WpfTestHost.RunAsync(async () =>
    {
        var dialogs = new ScriptedDialogService();
        await using var context = await CreateContextAsync(dialogs);
        var company = context.Company;
        var (window, editor) = await OpenEditorAsync(context, new BatchEditorRequest([company.JanKowalski, company.LukaszWisniewski])
        {
            Operations = [new SetAttributeOperation(Attr.Title, "Koordynator V2b")],
        });
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;
        await editor.PreviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        var activities = context.Services.GetRequiredService<ActivityCenterViewModel>();
        var toasts = context.Services.GetRequiredService<NotificationService>().Toasts;

        var first = new Gate();
        session.BeforeOperationAsync = operation => operation.IsWrite ? first.PassAsync() : Task.CompletedTask;
        ActivityViewModel activity;
        try
        {
            ControlHost.Press(window.ExecuteButton);
            await UiWait.UntilAsync(() => first.Reached.Task.IsCompleted && editor.Status.IsWorking, "the first object is being written");
            activity = Assert.Single(activities.Activities);
            toasts.Clear();

            // Close and continue in the background: the window closes at once, while the write is still held; the activity row runs on.
            dialogs.CloseAnswers.Enqueue(CloseAnswer.CloseAndContinue);
            window.Close();
            await UiWait.UntilAsync(() => closed, "the window closed at once");
            Assert.True(editor.IsDisposed);
            Assert.True(activity.IsRunning);
            Assert.False(activity.IsCancelling);
            Assert.True(Assert.Single(dialogs.CloseQuestions).CanContinue);
        }
        finally
        {
            first.Release();
        }

        // The execution goes on to its end without the window, and the connection says how it ended - once, with the domain.
        await UiWait.UntilAsync(() => !activity.IsRunning, "the execution ended");
        await UiWait.UntilAsync(() => toasts.Count > 0, "the outcome is a toast");
        Assert.Equal(2, session.Operations.Count(operation => operation.IsWrite));
        var toast = Assert.Single(toasts);
        Assert.Equal($"“{editor.ChangeTitle}” was executed in {domain}: 2 objects changed. The report is in the list of background operations.", toast.Message);
        Assert.Empty(dialogs.ErrorMessages);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheMainWindow_AsksOneQuestion_ListsTheBackgroundRun_NoBackgroundAnswer_StopWaitsBeforeClosing() => WpfTestHost.RunAsync(async () =>
    {
        var dialogs = new ScriptedDialogService();
        await using var context = await CreateContextAsync(dialogs);
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        var mainClosed = false;
        main.Closed += (_, _) => mainClosed = true;
        var company = context.Company;
        var (window, editor) = await OpenEditorAsync(context, new BatchEditorRequest([company.JanKowalski, company.LukaszWisniewski])
        {
            Operations = [new SetAttributeOperation(Attr.Title, "Koordynator V2b")],
        });
        var session = context.Directory.Session;
        await editor.PreviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The editor's execution is left in the background: its window closes, the run goes on.
        var first = new Gate();
        session.BeforeOperationAsync = operation => operation.IsWrite ? first.PassAsync() : Task.CompletedTask;
        try
        {
            ControlHost.Press(window.ExecuteButton);
            await UiWait.UntilAsync(() => first.Reached.Task.IsCompleted && editor.Status.IsWorking, "the first object is being written");
            dialogs.CloseAnswers.Enqueue(CloseAnswer.CloseAndContinue);
            window.Close();
            await UiWait.UntilAsync(() => editor.IsDisposed, "the editor closed");

            // The main window: one question, which lists the background run and says why nothing can go on without this window.
            dialogs.CloseAnswers.Enqueue(CloseAnswer.StopAndClose);
            main.Close();
            await UiWait.UntilAsync(() => dialogs.CloseQuestions.Count == 2, "the main window asks");
            var question = dialogs.CloseQuestions[1];
            Assert.False(question.CanContinue);
            Assert.Contains($"In the background: “{editor.ChangeTitle}”", question.Message, StringComparison.Ordinal);
            Assert.EndsWith("Closing this window ends its connection, so nothing can continue in the background.", question.Message, StringComparison.Ordinal);

            // Stop: the object in progress is finished first - the main window waits for it.
            await UiWait.UntilAsync(() => context.Services.GetRequiredService<ActivityCenterViewModel>().Activities is [{ IsCancelling: true }], "the run is stopping");
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.True(main.IsVisible);
            Assert.False(mainClosed);
        }
        finally
        {
            first.Release();
        }

        await UiWait.UntilAsync(() => mainClosed, "the main window closed after the run ended");
        var written = Assert.Single(session.Operations, operation => operation.IsWrite);
        Assert.Equal(company.JanKowalski, written.DistinguishedName);
        Assert.Empty(dialogs.ErrorMessages);
        context.AssertHealthy();
    });

    [Fact]
    public Task ClosingAWindowThatOnlyReads_NeedsNoQuestion_AndEndsTheRead() => WpfTestHost.RunAsync(async () =>
    {
        var dialogs = new ScriptedDialogService { ConfirmAnswer = false };
        await using var context = await CreateContextAsync(dialogs);
        var company = context.Company;
        var (window, editor) = await OpenEditorAsync(context, new BatchEditorRequest([company.JanKowalski, company.LukaszWisniewski])
        {
            Operations = [new SetAttributeOperation(Attr.Title, "Koordynator V2b")],
        });
        var session = context.Directory.Session;
        var closed = false;
        window.Closed += (_, _) => closed = true;

        // The check of the changes reads the objects; its first read waits for the test.
        var read = new Gate();
        session.BeforeOperationAsync = operation => operation.IsWrite ? Task.CompletedTask : read.PassAsync();
        Task previewing;
        try
        {
            previewing = editor.PreviewCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => read.Reached.Task.IsCompleted && editor.Status.IsWorking, "the changes are being checked");
            Assert.True(editor.Status.Operation is { IsWrite: false, CanCancel: true });

            // A read: the window closes at once - the answer of the dialogs, No, is never asked for.
            window.Close();
            await UiWait.UntilAsync(() => closed, "the window closed without a question");
        }
        finally
        {
            read.Release();
        }

        try
        {
            // The check was cancelled with the window; however the command reports that, it ends once the held read let go.
            await previewing.WaitAsync(HoldLimit);
        }
        catch (OperationCanceledException)
        {
            // The expected end of a cancelled check.
        }

        Assert.Empty(dialogs.Confirmations);
        Assert.Empty(dialogs.InfoMessages);
        Assert.True(editor.IsDisposed);
        Assert.DoesNotContain(session.Operations, operation => operation.IsWrite);
        context.AssertHealthy();
    });

    [Fact]
    public Task ClosingAWindowWhoseWriteCannotBeStopped_SaysItIsStillWorking_AndTheWindowStaysUntilItEnds() => WpfTestHost.RunAsync(async () =>
    {
        var dialogs = new ScriptedDialogService { ConfirmAnswer = true };
        await using var context = await CreateContextAsync(dialogs);
        var dialog = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateNewObject(ObjectKind.OrganizationalUnit, context.Company.CompanyOu);
        var window = (NewObjectWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(dialog);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        var session = context.Directory.Session;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        dialog.Name = "Projekty";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The creation waits for the test.
        var write = new Gate();
        session.BeforeOperationAsync = operation => operation.Kind == FakeOperationKind.Add ? write.PassAsync() : Task.CompletedTask;
        try
        {
            ControlHost.Press(window.CreateButton);
            await UiWait.UntilAsync(() => write.Reached.Task.IsCompleted && dialog.IsBusy && window.PageStatus.Kind == PageStatusKind.Working, "the object is being created");
            Assert.True(dialog.Status.Operation is { Writes: OperationWrites.Changes, CanCancel: false });

            // Closing the window cannot stop it, and asks nothing: it says that the window is still working, and stays.
            window.Close();
            await UiWait.UntilAsync(() => dialogs.InfoMessages.Count == 1, "the notice is shown");
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.StartsWith("Creating", dialogs.InfoMessages[0], StringComparison.Ordinal);
            Assert.Contains("cannot be stopped", dialogs.InfoMessages[0], StringComparison.Ordinal);
            Assert.Empty(dialogs.Confirmations);
            Assert.True(window.IsVisible);
            Assert.False(closed);
            Assert.False(dialog.Status.IsCancelling);
        }
        finally
        {
            write.Release();
        }

        // The object is created and the dialog closes itself, as before.
        await UiWait.UntilAsync(() => closed, "the dialog closed after the creation");
        Assert.NotNull(dialog.CreatedDn);
        Assert.True(session.Exists(dialog.CreatedDn!));
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>A connected test host whose confirmations are the test's, and whose batches run one object after another.</summary>
    private static async Task<UiTestContext> CreateContextAsync(ScriptedDialogService dialogs)
    {
        var context = await UiTestContext.CreateAsync(configure: services => services.Replace(ServiceDescriptor.Singleton<IDialogService>(dialogs)));
        await context.ConnectAsync();
        await context.Services.GetRequiredService<ISettingsStore>().UpdateAsync(s => s.BatchMaxParallelism = 1);
        return context;
    }

    private static async Task<(BatchEditorWindow Window, BatchEditorViewModel Editor)> OpenEditorAsync(UiTestContext context, BatchEditorRequest request)
    {
        var editor = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateBatchEditor(request);
        var window = (BatchEditorWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(editor);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        return (window, editor);
    }

    /// <summary>A call that waits for the test: it says it arrived, and goes on when the test releases it (and every later one goes on at once).</summary>
    private sealed class Gate
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PassAsync()
        {
            if (Reached.Task.IsCompleted)
            {
                return Task.CompletedTask;
            }

            Reached.TrySetResult();
            return Open.Task.WaitAsync(HoldLimit);
        }

        public void Release() => Open.TrySetResult();
    }
}
