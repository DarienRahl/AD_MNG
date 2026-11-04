using Microsoft.Extensions.Time.Testing;

namespace AdMng.Remote.Tests;

public class PolicyRefreshWatchTests
{
    private static readonly DateTimeOffset PreviousRun = new(2026, 10, 27, 7, 30, 0, TimeSpan.Zero);

    [Fact]
    public void WaitForNewRun_TheEndTimeOfThePreviousRun_IsNotCompletion()
    {
        // gpupdate /wait:0 has already exited, but the Group Policy state still holds the previous run.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
        var reads = 0;

        var end = PolicyRefreshWatch.WaitForNewRun(
            PreviousRun,
            () =>
            {
                reads++;
                return PreviousRun;
            },
            time.Advance,
            time,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.Null(end);
        Assert.Equal(300, reads);
    }

    [Fact]
    public void WaitForNewRun_ReturnsTheEndOfTheRunThatFinishedAfterTheLaunch()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
        var newRun = new DateTimeOffset(2026, 10, 27, 9, 0, 41, TimeSpan.Zero);
        var states = new Queue<DateTimeOffset?>([PreviousRun, PreviousRun, null, newRun]);

        var end = PolicyRefreshWatch.WaitForNewRun(
            PreviousRun, states.Dequeue, time.Advance, time, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(newRun, end);
        Assert.Empty(states);
    }

    [Fact]
    public void IsNewRun_ComparesWithTheEndTimeReadBeforeTheLaunch()
    {
        Assert.False(PolicyRefreshWatch.IsNewRun(PreviousRun, PreviousRun));
        Assert.False(PolicyRefreshWatch.IsNewRun(PreviousRun, PreviousRun.AddSeconds(-1)));
        Assert.False(PolicyRefreshWatch.IsNewRun(PreviousRun, null));
        Assert.False(PolicyRefreshWatch.IsNewRun(null, null));
        Assert.True(PolicyRefreshWatch.IsNewRun(PreviousRun, PreviousRun.AddTicks(1)));
        // A computer that never processed policy before.
        Assert.True(PolicyRefreshWatch.IsNewRun(null, PreviousRun));
    }

    [Fact]
    public void WaitForNewRun_StopsWhenCancelled()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => PolicyRefreshWatch.WaitForNewRun(
            PreviousRun, () => PreviousRun, _ => cancellation.Cancel(), time, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(2), cancellation.Token));
    }
}
