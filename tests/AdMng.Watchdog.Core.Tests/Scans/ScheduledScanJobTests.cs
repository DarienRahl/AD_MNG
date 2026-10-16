using AdMng.Core.Directory;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Scans;
using AdMng.Watchdog.Testing.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Core.Tests.Scans;

public class ScheduledScanJobTests
{
    [Fact]
    public async Task BeforeTheBaseline_NothingRuns()
    {
        await using var scenario = new DetectionScenario();

        var result = await scenario.Scans.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Null(scenario.Services.GetRequiredService<PermissionSelfCheck>().LastRunAt);
        Assert.Null(scenario.Health.Get(PermissionSelfCheck.UsnChanged));
    }

    [Fact]
    public async Task AfterTheBaseline_TheSelfCheckRunsFirstAndThenDaily()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var selfCheck = scenario.Services.GetRequiredService<PermissionSelfCheck>();

        await scenario.Scans.RunOnceAsync(TestContext.Current.CancellationToken);
        var first = selfCheck.LastRunAt;
        scenario.Advance(TimeSpan.FromHours(1));
        await scenario.Scans.RunOnceAsync(TestContext.Current.CancellationToken);
        var unchanged = selfCheck.LastRunAt;
        scenario.Advance(TimeSpan.FromHours(24));
        await scenario.Scans.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DetectionScenario.Start, first);
        Assert.Equal(first, unchanged);
        Assert.Equal(DetectionScenario.Start.AddHours(25), selfCheck.LastRunAt);
        Assert.Equal(HealthState.Ok, scenario.Health.Get(PermissionSelfCheck.ConfigurationPartition)!.State);
    }

    [Fact]
    public async Task UnreachableDirectory_IsAFailedIteration()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        scenario.Sessions.FailWith(DirectoryErrorKind.ServerUnavailable);

        var result = await scenario.Scans.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Failed);
        Assert.Equal("directory", result.FailureCode);
    }

    [Fact]
    public async Task Registration_ExposesBothJobs()
    {
        await using var scenario = new DetectionScenario();

        var jobs = scenario.Services.GetServices<IWatchdogJob>().ToList();

        Assert.Contains(jobs, j => j is ChangeDetectionEngine && j.Name == ChangeDetectionEngine.JobName);
        Assert.Contains(jobs, j => j is ScheduledScanJob && j.Name == ScheduledScanJob.JobName);
        Assert.Same(scenario.Engine, jobs.OfType<ChangeDetectionEngine>().Single());
        Assert.Equal(TimeSpan.FromMinutes(1), scenario.Scans.DefaultInterval);
    }
}
