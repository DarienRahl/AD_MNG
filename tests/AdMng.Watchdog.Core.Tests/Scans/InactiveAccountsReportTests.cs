using AdMng.Watchdog.Core.Scans;
using AdMng.Watchdog.Testing.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Core.Tests.Scans;

/// <summary>The weekly inactive accounts section (section 10.17): live queries, no events.</summary>
public class InactiveAccountsReportTests
{
    [Fact]
    public async Task InactiveUsersComputersAndPrivileged_AreCountedWithTheOldestFirst()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        scenario.Advance(TimeSpan.FromDays(200));
        scenario.Directory.SimulateSuccessfulLogon(scenario.Company.JanKowalski);
        scenario.Directory.SimulateSuccessfulLogon(scenario.Builder.DomainControllerDn);
        var report = scenario.Services.GetRequiredService<InactiveAccountsReport>();

        var summary = await report.BuildAsync(maxRows: 3, TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(7, summary.Users.Count);
        Assert.Equal(3, summary.Users.Rows.Count);
        Assert.DoesNotContain(summary.Users.Rows, r => r.Object.Dn == scenario.Company.JanKowalski);
        Assert.All(summary.Users.Rows, r => Assert.Null(r.LastLogon));
        Assert.Equal(90, summary.Users.ThresholdDays);
        Assert.Equal(3, summary.Computers.Count);
        Assert.Contains(summary.Computers.Rows, r => r.Object.Dn == scenario.Company.Computer1 && r.Container == scenario.Company.ComputersOu);
        var privileged = Assert.Single(summary.Privileged.Rows);
        Assert.Equal(scenario.Builder.AdministratorDn, privileged.Object.Dn);
        Assert.Equal(180, summary.Privileged.ThresholdDays);
        Assert.Empty(scenario.TakeEvents());
    }

    [Fact]
    public async Task YoungAccounts_AreNotReported()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        scenario.Advance(TimeSpan.FromDays(10));

        var summary = await scenario.Services.GetRequiredService<InactiveAccountsReport>().BuildAsync(50, TestContext.Current.CancellationToken);

        Assert.Equal(0, summary!.Users.Count);
        Assert.Equal(0, summary.Computers.Count);
    }

    [Fact]
    public async Task ReportDay_AndDisabledReport()
    {
        await using var scenario = new DetectionScenario();
        var report = scenario.Services.GetRequiredService<InactiveAccountsReport>();

        Assert.False(report.IsReportDay(DetectionScenario.Start));
        Assert.True(report.IsReportDay(DetectionScenario.Start.AddDays(5)));
        scenario.Config.Update(c => c with { Scans = c.Scans with { InactiveAccounts = c.Scans.InactiveAccounts with { Enabled = false } } });
        Assert.False(report.IsReportDay(DetectionScenario.Start.AddDays(5)));
        Assert.Null(await report.BuildAsync(50, TestContext.Current.CancellationToken));
    }
}
