using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.TestDoubles;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Shell;

/// <summary>
/// What the main window offers the two things that came with the owner's decision 6 (track V, stage V2b-5b): the holder in
/// front, which the taskbar button follows (<see cref="ShellViewModel.FrontStatus"/>), and the holders of every tab, which the
/// close question of a running write looks at (<see cref="ShellViewModel.StatusParts"/>, closing a tab).
/// </summary>
public class ShellRunningWorkTests
{
    [Fact]
    public async Task TheHolderInFront_IsTheOneOfTheSelectedTab_AndFollowsTheSelection()
    {
        var harness = await TestHarness.ConnectedAsync();
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(Ksiegowosc));
        var accounting = shell.SelectedDocument!;
        Assert.Same(accounting.Status, shell.FrontStatus);
        var raised = new List<string?>();
        shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await shell.OpenLocationInNewTabAsync(ListLocation.ForContainer(It));
        var it = shell.SelectedDocument!;
        Assert.NotSame(accounting, it);
        Assert.Same(it.Status, shell.FrontStatus);
        Assert.Contains(nameof(ShellViewModel.FrontStatus), raised);

        shell.SelectedDocument = accounting;
        Assert.Same(accounting.Status, shell.FrontStatus);
        Assert.IsAssignableFrom<IHasFrontStatus>(shell);
    }

    [Fact]
    public async Task TheParts_AreTheHoldersOfEveryTab_ThoseInFrontAndThoseNot()
    {
        var harness = await TestHarness.ConnectedAsync();
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(Ksiegowosc));
        var accounting = shell.SelectedDocument!;
        await shell.OpenLocationInNewTabAsync(ListLocation.ForContainer(It));
        var it = shell.SelectedDocument!;

        Assert.Equal(
            [accounting.Status, it.Status],
            RunningWork.HoldersOf(shell).Where(holder => holder == accounting.Status || holder == it.Status).OrderBy(holder => holder == it.Status));

        // A write in the tab that is not in front is found all the same.
        shell.SelectedDocument = it;
        using var write = accounting.Status.Begin("Zapisywanie…", new PageStatusLog(L.WatchdogConfigStatus.Saving), writes: OperationWrites.Other);
        Assert.True(RunningWork.HasRunningWrites(shell));
        write.Complete(null);
        Assert.False(RunningWork.HasRunningWrites(shell));
    }

    [Fact]
    public async Task ClosingATab_ThatReads_NeedsNoQuestion_AndTheTabGoes()
    {
        var harness = await TestHarness.ConnectedAsync();
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(Ksiegowosc));
        await shell.OpenLocationInNewTabAsync(ListLocation.ForContainer(It));
        var second = shell.SelectedDocument!;
        using var read = second.Status.Begin("Wczytywanie…", new PageStatusLog(L.ListStatus.Reading));

        await shell.CloseTabCommand.ExecuteAsync(second);

        Assert.DoesNotContain(second, shell.Documents);
        Assert.Empty(harness.Dialogs.Shown);
    }

    [Fact]
    public async Task ClosingATab_ThatWrites_AsksFirst_NoKeepsIt_YesStopsItAndClosesAfterTheEnd()
    {
        var harness = await TestHarness.ConnectedAsync();
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(Ksiegowosc));
        await shell.OpenLocationInNewTabAsync(ListLocation.ForContainer(It));
        var second = shell.SelectedDocument!;
        using var write = second.Status.Begin("Aktualizowanie…", new PageStatusLog("A"), writes: OperationWrites.Other);

        // No: the tab stays and the operation runs on.
        harness.Dialogs.ConfirmAnswers.Enqueue(false);
        await shell.CloseTabCommand.ExecuteAsync(second);
        Assert.Contains(second, shell.Documents);
        Assert.False(write.IsCancelling);
        Assert.StartsWith("confirm:Aktualizowanie…\n\n", Assert.Single(harness.Dialogs.Shown), StringComparison.Ordinal);

        // Yes: Stop through the holder, the tab goes when the operation has ended - not before.
        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        var closing = shell.CloseTabCommand.ExecuteAsync(second);
        Assert.True(write.IsCancelling);
        Assert.False(closing.IsCompleted);
        Assert.Contains(second, shell.Documents);
        write.Complete(null);
        await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(second, shell.Documents);
    }

    [Fact]
    public async Task ClosingATab_ThatWritesAndCannotBeStopped_KeepsItAndSaysSo()
    {
        var harness = await TestHarness.ConnectedAsync();
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(Ksiegowosc));
        await shell.OpenLocationInNewTabAsync(ListLocation.ForContainer(It));
        var second = shell.SelectedDocument!;
        using var saving = second.Status.Begin("Zapisywanie…", new PageStatusLog("A"), canCancel: false, writes: OperationWrites.Other);

        await shell.CloseTabCommand.ExecuteAsync(second);

        Assert.Contains(second, shell.Documents);
        Assert.StartsWith("info:Zapisywanie…\n\n", Assert.Single(harness.Dialogs.Shown), StringComparison.Ordinal);
        Assert.False(saving.IsCancelling);
    }
}
