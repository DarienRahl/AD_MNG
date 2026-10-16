using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Detection;

public class BaselineTests
{
    [Fact]
    public async Task Baseline_StoresSnapshotsWithoutChangeEvents()
    {
        await using var scenario = new DetectionScenario();

        var result = await scenario.RunCycleAsync();

        Assert.Equal(DetectionMode.Baseline, result.Mode);
        var events = scenario.TakeEvents();
        var completed = Assert.Single(events);
        Assert.Equal(WatchEventKind.HealthBaselineCompleted, completed.Kind);
        Assert.NotNull(scenario.Snapshot(scenario.Company.JanKowalski));
        Assert.NotNull(scenario.Snapshot(scenario.Company.GroupIt));
        Assert.NotNull(scenario.Snapshot(scenario.Company.ItOu));
        Assert.Equal(3, (await scenario.Store.GetMembersAsync(scenario.Snapshot(scenario.Company.GroupSales)!.ObjectGuid, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task Baseline_MovesTheWatermarkToTheUsnReadBeforeTheScan()
    {
        await using var scenario = new DetectionScenario();
        var highest = scenario.Directory.HighestCommittedUsn;

        await scenario.RunCycleAsync();

        var state = Assert.IsType<DcState>(scenario.Store.State);
        Assert.Equal(highest, state.HighestUsn);
        Assert.Equal(scenario.Directory.CurrentInvocationId, state.InvocationId);
        Assert.Equal(DetectionScenario.Start, state.BaselineCompletedAt);
        Assert.Equal(DetectionScenario.Start, state.LastFullSyncAt);
        Assert.NotNull(scenario.Store.Privileges);
    }

    [Fact]
    public async Task Baseline_CommitsChunksWithoutDcStateAndTheStateLast()
    {
        await using var scenario = new DetectionScenario();

        await scenario.RunCycleAsync();

        var commits = scenario.Store.Commits;
        Assert.True(commits.Count >= 2);
        Assert.All(commits.Take(commits.Count - 1), c => Assert.Null(c.NewState));
        Assert.NotNull(commits[^1].NewState);
        Assert.True(commits[^1].IsBaseline);
    }

    [Fact]
    public async Task SecondCycleWithoutChanges_IsNoChangeAndWritesNothing()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var commits = scenario.Store.Commits.Count;

        var result = await scenario.RunCycleAsync();

        Assert.Equal(DetectionMode.NoChange, result.Mode);
        Assert.Empty(scenario.TakeEvents());
        Assert.Equal(commits, scenario.Store.Commits.Count);
    }
}
