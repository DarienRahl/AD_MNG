using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// logonHours through the real change pipeline and LDAP session: the planner reads the binary value as hex, the executor
/// writes 21 bytes with compare-and-swap and the rollback restores the previous schedule.
/// </summary>
public sealed class LogonHoursTests(LabFixture lab)
{
    private static readonly DayOfWeek[] Workdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    [Fact]
    public async Task Schedule_IsWrittenAsBytes_ReadBack_AndRolledBack()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Godziny logowania"));
        var dn = await lab.CreateUserAsync(ou, "Halina Żółkiewska");
        var office = LogonHours.FromLocalGrid(LogonHoursGrid.Create((day, hour) => Workdays.Contains(day) && hour is >= 7 and < 15), 0);
        var planner = new ChangePlanner(new TemplateEngine(), new PasswordGenerator(), TimeProvider.System);
        var executor = new BatchExecutor(new InMemoryAuditLog(), TimeProvider.System);

        // A new account has no restriction: permitting every hour changes nothing.
        var unchanged = await planner.PlanAsync(lab.Admin, new BatchRequest([dn], [new SetLogonHoursOperation(LogonHours.All)]), null, null, ct);
        Assert.True(Assert.Single(unchanged.Objects).IsNoOp);

        var plan = await planner.PlanAsync(lab.Admin, new BatchRequest([dn], [new SetLogonHoursOperation(office)]), null, null, ct);
        Assert.Equal(0, plan.ErrorCount);
        var result = await executor.ExecuteAsync(lab.Admin, plan, null, null, ct);
        Assert.Equal(1, result.SucceededCount);

        var written = await lab.Admin.GetAsync(dn, [Attr.LogonHours], ct);
        Assert.Equal(office.ToBytes(), written!.GetBytes(Attr.LogonHours));
        Assert.IsType<byte[]>(written.Attributes[Attr.LogonHours][0]);

        // The planner reads the stored bytes back as the same schedule (no-op), and a change is a compare-and-swap.
        var again = await planner.PlanAsync(lab.Admin, new BatchRequest([dn], [new SetLogonHoursOperation(office)]), null, null, ct);
        Assert.True(Assert.Single(again.Objects).IsNoOp);
        var everything = await planner.PlanAsync(lab.Admin, new BatchRequest([dn], [new SetLogonHoursOperation(LogonHours.All)]), null, null, ct);
        var allResult = await executor.ExecuteAsync(lab.Admin, everything, null, null, ct);
        Assert.Equal(1, allResult.SucceededCount);
        Assert.Equal(LogonHours.All.ToBytes(), (await lab.Admin.GetAsync(dn, [Attr.LogonHours], ct))!.GetBytes(Attr.LogonHours));

        var rollback = await executor.ExecuteAsync(lab.Admin, new RollbackPlanner().CreateRollbackPlan(allResult), null, null, ct);
        Assert.Equal(1, rollback.SucceededCount);
        Assert.Equal(office.ToBytes(), (await lab.Admin.GetAsync(dn, [Attr.LogonHours], ct))!.GetBytes(Attr.LogonHours));

        var first = await executor.ExecuteAsync(lab.Admin, new RollbackPlanner().CreateRollbackPlan(result), null, null, ct);
        Assert.Equal(1, first.SucceededCount);
        Assert.False((await lab.Admin.GetAsync(dn, [Attr.LogonHours], ct))!.Has(Attr.LogonHours));
    }
}
