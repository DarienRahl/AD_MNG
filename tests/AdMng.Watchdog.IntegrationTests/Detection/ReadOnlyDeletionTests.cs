using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.IntegrationTests.Infrastructure;

namespace AdMng.Watchdog.IntegrationTests.Detection;

/// <summary>
/// Deletions seen by the read-only engine (spec 10.9, 22.4): <c>odczyt.test</c> cannot read tombstones (Appendix B), so
/// the incremental cycles stay silent, the daily reconciliation reports ObjectDisappeared, and the permission
/// self-check shows <c>permission.deletedObjects</c> as a Warning.
/// </summary>
public sealed class ReadOnlyDeletionTests(ReadOnlyEngineFixture engine, LabFixture lab) : IClassFixture<ReadOnlyEngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EngineHost Host => engine.Host;

    [Fact]
    public async Task DeletedUser_IsObjectDisappearedAtTheReconciliation_AndDeletedObjectsIsAPermissionWarning()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Zniknięcia", Ct);
        var user = await lab.Writer.CreateUserAsync(ou, "Krzysztof", "Zieliński", TestNames.Password(), cancellationToken: Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        await lab.Writer.DeleteAsync(user.Dn, subtree: false, Ct);
        await Host.RunDetectionAsync(Ct);
        EventAssert.None(await Host.EventsAsync(ou, mark, Ct), user.Dn);

        await Host.RunReconciliationAsync(Ct);
        var gone = EventAssert.Single(await Host.EventsAsync(ou, mark, Ct), WatchEventKind.ObjectDisappeared, user.Dn);
        Assert.Equal(DetectionSource.Reconciliation, gone.Source);

        await Host.RunScansAsync(Ct);
        var check = Assert.Single(Host.Health.Checks, c => c.Code == PermissionSelfCheck.DeletedObjects);
        Assert.Equal(HealthState.Warning, check.State);
    }
}
