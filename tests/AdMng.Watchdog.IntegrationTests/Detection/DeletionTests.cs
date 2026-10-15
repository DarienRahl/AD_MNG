using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.IntegrationTests.Infrastructure;

namespace AdMng.Watchdog.IntegrationTests.Detection;

/// <summary>
/// Deletions seen by an engine that may read <c>CN=Deleted Objects</c> (Administrator, Show Deleted): one ObjectDeleted
/// at once – for a computer with a child object too (spec 10.9, 22.4).
/// </summary>
public sealed class DeletionTests(AdministratorEngineFixture engine, LabFixture lab) : IClassFixture<AdministratorEngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EngineHost Host => engine.Host;

    [Fact]
    public async Task DeletedUser_IsObjectDeleted()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Usunięcia", Ct);
        var user = await lab.Writer.CreateUserAsync(ou, "Grażyna", "Dąbrowska", TestNames.Password(), cancellationToken: Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        await lab.Writer.DeleteAsync(user.Dn, subtree: false, Ct);
        await Host.RunDetectionAsync(Ct);

        var deleted = EventAssert.Single(await Host.EventsAsync(ou, mark, Ct), WatchEventKind.ObjectDeleted, user.Dn);
        Assert.Equal(WatchObjectKind.User, deleted.Object.Kind);
        Assert.Equal(user.SamAccountName, deleted.Object.SamAccountName);
    }

    [Fact]
    public async Task DeletedComputerWithAChildObject_IsOneObjectDeleted()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Usunięte komputery", Ct);
        var computer = await lab.Writer.CreateComputerAsync(ou, "WDIT-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), Ct);
        await lab.Writer.CreateServiceConnectionPointAsync(computer, "Usługa Zdalna", Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        await lab.Writer.DeleteAsync(computer, subtree: true, Ct);
        await Host.RunDetectionAsync(Ct);

        var events = await Host.EventsAsync(ou, mark, Ct);
        EventAssert.Single(events, WatchEventKind.ObjectDeleted, computer);
        Assert.True(events.Count == 1, $"one event for the computer and its child: {EventAssert.Describe(events)}");
    }
}
