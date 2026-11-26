using System.Windows.Shell;
using AdMng.App.Behaviors;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// The progress of the taskbar button of a window (track V, stage V2b-5b, owner's decision 6; docs/dev/v-interface-renewal.md
/// 6.7), through the real windows over the fake directory: the button follows the status line - how far an execution is, that a
/// read cannot say how far, that an operation ended with a state that lasts - and says nothing a window without a status line
/// says. The delay before it appears is the line's (<see cref="TaskbarClock"/> moves it, nobody waits); a slow domain controller
/// is a call the test holds without blocking a thread and lets go when it has looked. Every state that is asserted lasts until the
/// test changes it.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class TaskbarProgressUiTests
{
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task AnExecution_PutsItsProgressOnTheButton_FirstWithoutACount_ThenThePartDone_AndTakesItOffAtTheEnd() => WpfTestHost.RunAsync(async () =>
    {
        using var clock = new TaskbarClock();
        await using var context = await CreateContextAsync();
        var company = context.Company;
        var (window, editor) = await OpenEditorAsync(context, new BatchEditorRequest([company.JanKowalski, company.LukaszWisniewski])
        {
            Operations = [new SetAttributeOperation(Attr.Title, "Koordynator V2b")],
        });
        var session = context.Directory.Session;
        await editor.PreviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(BatchEditorStep.Preview, editor.Step);

        // At rest the button has no progress, and was never given any.
        Assert.Equal(TaskbarProgressState.None, TaskbarProgress.StateOf(window));
        Assert.True(window.TaskbarItemInfo is null or { ProgressState: TaskbarItemProgressState.None });

        // One object after another; the write of each waits for the test.
        var first = new Gate();
        var second = new Gate();
        var writes = 0;
        session.BeforeOperationAsync = operation => !operation.IsWrite ? Task.CompletedTask : Interlocked.Increment(ref writes) switch
        {
            1 => first.PassAsync(),
            2 => second.PassAsync(),
            _ => Task.CompletedTask,
        };
        TaskbarItemInfo info;
        try
        {
            ControlHost.Press(window.ExecuteButton);
            await UiWait.UntilAsync(
                () => first.Reached.Task.IsCompleted && editor.Step == BatchEditorStep.Executing && editor.Status.IsWorking, "the first object is being written");

            // Working, and no object is done: the line cannot say how far it is, and it shows "Working" only after its delay - so
            // does the button.
            Assert.Equal(TaskbarProgressKind.None, TaskbarProgress.StateOf(window).Kind);
            clock.AdvanceDelay();
            info = Assert.IsType<TaskbarItemInfo>(window.TaskbarItemInfo);
            Assert.Equal(TaskbarItemProgressState.Indeterminate, info.ProgressState);
            Assert.Equal(TaskbarProgressState.Indeterminate, TaskbarProgress.StateOf(window));

            // One of two is done: the part done, green.
            first.Release();
            await UiWait.UntilAsync(
                () => second.Reached.Task.IsCompleted && editor.Status.Current.Progress?.Fraction == 0.5, "the first object is done and the second one waits");
            Assert.Equal(TaskbarItemProgressState.Normal, info.ProgressState);
            Assert.Equal(0.5, info.ProgressValue, 5);
        }
        finally
        {
            first.Release();
            second.Release();
        }

        await UiWait.UntilAsync(() => editor.Step == BatchEditorStep.Results && editor.Status.Current.Kind == PageStatusKind.Done, "the execution ended");
        session.BeforeOperationAsync = null;

        // The result says what happened, in the line; the button has nothing to add.
        Assert.Equal(TaskbarItemProgressState.None, info.ProgressState);
        Assert.Equal(0d, info.ProgressValue);
        Assert.Equal(TaskbarProgressState.None, TaskbarProgress.StateOf(window));
        context.AssertHealthy();
    });

    [Fact]
    public Task TheMainWindow_FollowsTheTabInFront_KeepsItsDot_AndAReadThatFailedAfterTheButtonShowedItIsRedUntilTheNextRead() => WpfTestHost.RunAsync(async () =>
    {
        using var clock = new TaskbarClock();
        await using var context = await UiTestContext.CreateAsync();

        // A container of its own, which nothing else reads, and which refuses once its read is let go.
        var broken = context.Directory.AddOu("Awaria", context.Company.CompanyOu);
        var (window, document) = await OpenMainAsync(context, ListLocation.ForContainer(context.Company.ItOu));

        // The taskbar button of the main window already carries the dot of its connection: the progress joins it.
        var info = Assert.IsType<TaskbarItemInfo>(window.TaskbarItemInfo);
        var overlay = info.Overlay;
        Assert.NotNull(overlay);
        Assert.Equal(TaskbarItemProgressState.None, info.ProgressState);

        var session = context.Directory.Session;
        session.FailNext(FakeOperationKind.Search, broken, DirectoryErrorKind.ServerUnavailable);
        var held = HoldTheReadOf(session, broken);
        Task reading;
        try
        {
            reading = document.NavigateAsync(ListLocation.ForContainer(broken));
            await UiWait.UntilAsync(() => held.Reached.Task.IsCompleted && document.IsLoading && document.Status.IsWorking, "the read waits");

            // The list says "Working" after 150 ms; the button with it - not before.
            Assert.Equal(TaskbarItemProgressState.None, info.ProgressState);
            clock.AdvanceDelay();
            Assert.Equal(TaskbarItemProgressState.Indeterminate, info.ProgressState);

            // Another tab comes to the front: it says nothing, so the button does not either - the holder in front is the one
            // that counts. The tab that reads takes the button back, after the delay, when it is in front again.
            await window.Shell.OpenLocationInNewTabAsync(ListLocation.ForContainer(context.Company.EmployeesOu));
            var other = Assert.IsType<ObjectListViewModel>(window.Shell.SelectedTab);
            await UiWait.UntilAsync(() => !other.IsLoading, "the second tab is read");
            Assert.NotSame(document, other);
            Assert.Equal(TaskbarItemProgressState.None, info.ProgressState);
            window.Shell.SelectedDocument = document;
            Assert.Equal(TaskbarItemProgressState.None, info.ProgressState);
            clock.AdvanceDelay();
            Assert.Equal(TaskbarItemProgressState.Indeterminate, info.ProgressState);
        }
        finally
        {
            held.Release();
        }

        await reading.WaitAsync(HoldLimit);
        await UiWait.UntilAsync(() => !document.IsLoading && document.Error is not null, "the read failed");

        // "Could not be read" is the state of the page that lasts; the read that ended with it was on the button, so the button
        // goes red where it stopped - until the page says anything else.
        Assert.Equal(PageStatusKind.Error, document.Status.Current.Kind);
        Assert.False(document.Status.Current.CanClose);
        Assert.Equal(TaskbarItemProgressState.Error, info.ProgressState);
        Assert.Equal(1d, info.ProgressValue);
        Assert.Same(overlay, info.Overlay);

        await document.NavigateAsync(ListLocation.ForContainer(context.Company.ItOu));
        await UiWait.UntilAsync(() => !document.IsLoading && document.Error is null, "the next read ended");
        Assert.Equal(TaskbarItemProgressState.None, info.ProgressState);
        Assert.Equal(0d, info.ProgressValue);
        context.AssertHealthy();
    });

    [Fact]
    public Task AWindowWithoutAStatusLine_KeepsItsButtonAsItIs() => WpfTestHost.RunAsync(async () =>
    {
        using var clock = new TaskbarClock();
        await using var context = await UiTestContext.CreateAsync();
        var localizer = context.Services.GetRequiredService<ILocalizer>();
        var windows = context.Services.GetRequiredService<IWindowService>();
        var window = (MessageDialogWindow)windows.CreateWindow(MessageDialogViewModel.Information("Informacja", "Zażółć gęślą jaźń.", localizer));
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync();

        clock.AdvanceDelay();
        Assert.Equal(TaskbarProgressState.None, TaskbarProgress.StateOf(window));
        Assert.Null(window.TaskbarItemInfo);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A connected test host: confirmations are answered with "yes" without a window, and the batch runs one object after
    /// another, so "1 of 2" is a state a test can hold.
    /// </summary>
    private static async Task<UiTestContext> CreateContextAsync()
    {
        var dialogs = new ScriptedDialogService { ConfirmAnswer = true };
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

    private static async Task<(MainWindow Window, ObjectListViewModel Document)> OpenMainAsync(UiTestContext context, ListLocation location)
    {
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await window.Shell.NavigateAsync(location);
        await UiTestSupport.WaitForIdleAsync();
        var document = Assert.IsType<ObjectListViewModel>(window.Shell.SelectedTab);
        await UiWait.UntilAsync(() => !document.IsLoading, "the first read of the container");
        Assert.Null(document.Error);
        return (window, document);
    }

    /// <summary>The search of the container waits (no thread is blocked) until the test lets it go.</summary>
    private static Gate HoldTheReadOf(FakeDirectorySession session, string containerDn)
    {
        var gate = new Gate();
        session.BeforeOperationAsync = operation =>
            operation.Kind == FakeOperationKind.Search
            && string.Equals(operation.DistinguishedName, containerDn, StringComparison.OrdinalIgnoreCase)
            && !gate.Reached.Task.IsCompleted
                ? gate.PassAsync()
                : Task.CompletedTask;
        return gate;
    }

    /// <summary>A call that waits for the test: it says it arrived, and goes on when the test releases it.</summary>
    private sealed class Gate
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PassAsync()
        {
            Reached.TrySetResult();
            return Open.Task.WaitAsync(HoldLimit);
        }

        public void Release() => Open.TrySetResult();
    }
}
