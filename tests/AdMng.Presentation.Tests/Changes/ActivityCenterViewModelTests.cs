using System.Runtime.CompilerServices;
using AdMng.Core.Batch;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Tests.TestDoubles;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Changes;

public class ActivityCenterViewModelTests
{
    [Fact]
    public async Task FinishedActivity_KeepsOnlyItsCounters_ThePlanWithPasswordsBecomesCollectable()
    {
        var harness = await TestHarness.ConnectedAsync();

        var (plan, activity) = await ResetTwoPasswordsAsync(harness.Activities);

        // Off the stack of the completed executions (their frames may still reference the plan while they unwind).
        await Task.Yield();
        for (var i = 0; i < 10 && plan.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.False(plan.IsAlive, "The finished activity keeps the plan (with its passwords) reachable.");
        Assert.Same(activity, Assert.Single(harness.Activities.Activities));
        Assert.False(activity.IsRunning);
        Assert.Equal(2, activity.SucceededCount);
        Assert.Equal(0, activity.FailedCount);
        Assert.Equal("Completed (2 objects)", activity.StatusText);
        Assert.True(activity.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SessionChange_DropsFinishedActivities_ButKeepsRunningOnes()
    {
        var harness = await TestHarness.ConnectedAsync();
        var (finished, _) = await ResetTwoPasswordsAsync(harness.Activities);
        var (running, execution) = harness.Activities.Start("Wyłączanie kont", 1, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });
        Assert.Equal(2, harness.Activities.Activities.Count);

        await harness.Sessions.DisconnectAsync();

        Assert.Same(running, Assert.Single(harness.Activities.Activities));
        running.CancelCommand.Execute(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await running.Completion;
        Assert.Equal("The operation was cancelled.", running.StatusText);
    }

    // ---- The window of an activity (the owner's decision D-A) -------------------------------------------------------

    [Fact]
    public async Task LeavingAndReturning_AreDecidedExactlyOnce_WhenTheyMeetOnTwoThreads()
    {
        var harness = await TestHarness.ConnectedAsync();
        for (var i = 0; i < 200; i++)
        {
            var (activity, execution) = harness.Activities.Start("Disable accounts", 1, (_, _) => Task.FromResult(Succeeded(new ChangePlan { Objects = [] })));
            activity.AttachWindow();
            using var start = new Barrier(2);
            var leave = Task.Run(() =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                return activity.TryLeaveWindow();
            });
            var back = Task.Run(() =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                return activity.TryReturnToWindow();
            });

            // Exactly one side wins: the window left it (the connection reports), or its result came back (the window reports).
            var left = await leave;
            var returned = await back;
            Assert.True(left ^ returned, $"round {i}: left={left}, returned={returned}");
            Assert.Equal(left, activity.HasLeftWindow);
            Assert.False(activity.HasWindow);
            await execution;
        }
    }

    [Fact]
    public async Task HasWindow_FollowsTheWindow_AndOnlyTheOthersAreTheConnectionsBackgroundWork()
    {
        var harness = await TestHarness.ConnectedAsync();
        var gate = new TaskCompletionSource<BatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (followed, _) = harness.Activities.Start("Followed", 1, (_, _) => gate.Task);
        var (left, _) = harness.Activities.Start("Left", 1, (_, _) => gate.Task);
        var (simple, _) = harness.Activities.Start("Simple", 1, (_, _) => gate.Task);
        followed.AttachWindow();
        left.AttachWindow();

        Assert.True(followed.HasWindow);
        Assert.True(left.TryLeaveWindow());
        Assert.False(left.HasWindow);
        Assert.False(simple.HasWindow);

        // An activity nobody followed is its caller's: its result goes back to it, and the connection does not report it.
        Assert.False(simple.TryLeaveWindow());

        Assert.Equal<object>([simple, left], [.. harness.Activities.RunningInBackground]);
        var line = Assert.IsAssignableFrom<Presentation.Feedback.IBackgroundWork>(left).Line;
        Assert.Equal("In the background: “Left”, 0 / 1", line);
        gate.SetResult(Succeeded(new ChangePlan { Objects = [] }));
        await Task.WhenAll(followed.Completion, left.Completion, simple.Completion);
        Assert.Empty(harness.Activities.RunningInBackground);
        Assert.True(simple.TryReturnToWindow());
        Assert.True(followed.TryReturnToWindow());
        Assert.False(left.TryReturnToWindow());
    }

    [Fact]
    public async Task StoppingBecauseTheConnectionCloses_CancelsIt_AndSuppressesItsReport()
    {
        var harness = await TestHarness.ConnectedAsync();
        var (activity, execution) = harness.Activities.Start("Disable accounts", 1, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });
        var work = Assert.IsAssignableFrom<Presentation.Feedback.IBackgroundWork>(activity);
        Assert.False(work.IsStopping);

        work.StopBecauseTheConnectionCloses();

        Assert.True(work.IsStopping);
        Assert.True(activity.IsCancelling);
        Assert.True(activity.IsReportSuppressed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await work.Ended;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Plan, ActivityViewModel Activity)> ResetTwoPasswordsAsync(ActivityCenterViewModel center)
    {
        var plan = new ChangePlan
        {
            Title = "Reset hasła",
            Objects = [Reset(Lukasz, "Łukasz Żółć", "Źdźbło-Żółć-2026!"), Reset(Ewa, "Ewa Kowalska", "Gęś-Pięść-2026!")],
        };
        var (activity, execution) = center.Start(plan.Title, plan.ExecutableCount, (_, _) => Task.FromResult(Succeeded(plan)));

        var result = await execution;
        Assert.Equal(2, result.SucceededCount);
        Assert.True(result.Plan.ContainsPasswords);
        await activity.Completion;
        return (new WeakReference(plan), activity);
    }

    private static ObjectPlan Reset(string dn, string name, string password) => new()
    {
        DistinguishedName = dn,
        DisplayName = name,
        Kind = ObjectKind.User,
        Changes = [new PlannedChange { Action = ChangeAction.ResetPassword, Password = password }],
    };

    private static BatchResult Succeeded(ChangePlan plan)
    {
        var now = TestHarness.Now;
        return new BatchResult(
            plan,
            [.. plan.Objects.Select(o => new ObjectResult(o, ObjectResultStatus.Succeeded, [.. o.Changes.Select(c => new ChangeResult(c, true))], o.DistinguishedName, now, now))],
            "dc01.contoso.test",
            "CONTOSO\\helpdesk",
            now,
            now,
            WasCancelled: false);
    }
}
