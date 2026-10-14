using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>The creation settle window (section 10.6.3).</summary>
public class CreationSettlerTests
{
    private const int Normal = (int)UserAccountControl.NormalAccount;
    private const int NewAdUser = (int)(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable | UserAccountControl.PasswordNotRequired);

    [Fact]
    public async Task NewAdUserThenPasswordThenEnable_IsOneUserCreatedWithTheFinalState()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        var user = scenario.Builder.AddUser("Wiesław", "Grabowski", scenario.Company.SalesOu, Uac(NewAdUser));
        Assert.Empty(await scenario.CycleEventsAsync());
        scenario.Advance(TimeSpan.FromSeconds(20));
        await scenario.ResetPasswordAsync(user);
        Assert.Empty(await scenario.CycleEventsAsync());
        scenario.Advance(TimeSpan.FromSeconds(20));
        await scenario.ReplaceAsync(user, Attr.UserAccountControl, Normal);
        Assert.Empty(await scenario.CycleEventsAsync());
        scenario.Advance(TimeSpan.FromMinutes(2));
        var events = await scenario.CycleEventsAsync();

        var created = Assert.Single(events);
        Assert.Equal(WatchEventKind.UserCreated, created.Kind);
        var uac = Assert.Single(created.Changes, c => c.Attribute.Equals(Attr.UserAccountControl, StringComparison.OrdinalIgnoreCase));
        Assert.Equal([Normal.ToString(System.Globalization.CultureInfo.InvariantCulture)], uac.NewValues);
        Assert.Empty(uac.OldValues);
    }

    [Fact]
    public async Task RiskyFinalBits_AddAnAccountFlagsChangedHighToTheChangeSet()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        scenario.Builder.AddUser("Zdzisław", "Pietrzak", scenario.Company.ItOu, Uac(Normal | (int)UserAccountControl.DontRequirePreauth));
        var events = await scenario.SettleAsync();

        var created = Assert.Single(events, e => e.Kind == WatchEventKind.UserCreated);
        var flags = Assert.Single(events, e => e.Kind == WatchEventKind.AccountFlagsChanged);
        Assert.Equal(Severity.High, flags.Severity);
        Assert.Equal(created.ChangeSetId, flags.ChangeSetId);
        Assert.Contains("DONT_REQ_PREAUTH", Assert.Single(flags.Changes).FlagsAdded);
    }

    [Fact]
    public async Task PasswordNotRequiredOnAStillDisabledAccount_IsLow()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        scenario.Builder.AddUser("Halina", "Jabłońska", scenario.Company.AccountingOu, Uac(NewAdUser));
        var events = await scenario.SettleAsync();

        var flags = Assert.Single(events, e => e.Kind == WatchEventKind.AccountFlagsChanged);
        Assert.Equal(Severity.Low, flags.Severity);
    }

    [Fact]
    public async Task UnconstrainedDelegationOnANewComputer_IsDelegationChanged()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        scenario.Builder.AddComputer("SRV-APP01", scenario.Company.ComputersOu, Uac((int)(UserAccountControl.WorkstationTrustAccount | UserAccountControl.TrustedForDelegation)));
        var events = await scenario.SettleAsync();

        Assert.Single(events, e => e.Kind == WatchEventKind.ComputerCreated);
        Assert.Equal(Severity.Critical, Assert.Single(events, e => e.Kind == WatchEventKind.DelegationChanged).Severity);
    }

    [Fact]
    public async Task DeletedInsideTheWindow_IsCreatedThenDeleted()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        var user = scenario.Builder.AddUser("Tymczasowy", "Użytkownik", scenario.Company.SalesOu);
        await scenario.CycleEventsAsync();

        await scenario.DeleteAsync(user);
        var events = await scenario.CycleEventsAsync();

        Assert.Equal([WatchEventKind.UserCreated, WatchEventKind.ObjectDeleted], events.Select(e => e.Kind));
        Assert.Equal(user, events[1].PreviousDn);
    }

    [Fact]
    public async Task ZeroWindow_EmitsTheCreatedEventAtOnce()
    {
        await using var scenario = new DetectionScenario(configuration: new WatchdogConfiguration
        {
            Directory = new DirectorySettings { CreationSettleWindow = TimeSpan.Zero },
        });
        await scenario.BaselineAsync();

        var group = scenario.Builder.AddGroup("GG_Natychmiast", scenario.Company.GroupsOu);
        var created = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.GroupCreated, created.Kind);
        Assert.Equal(group, created.Object.Dn);
    }

    [Fact]
    public async Task SettledCreation_IsEmittedAlsoByAnIdleCycle()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        scenario.Builder.AddUser("Kazimierz", "Wróbel", scenario.Company.ItOu);
        await scenario.CycleEventsAsync();
        scenario.Advance(TimeSpan.FromMinutes(5));

        var result = await scenario.RunCycleAsync();

        Assert.Equal(AdMng.Watchdog.Core.Model.DetectionMode.NoChange, result.Mode);
        Assert.Equal(WatchEventKind.UserCreated, Assert.Single(scenario.TakeEvents()).Kind);
    }

    private static Dictionary<string, object?> Uac(int value) => new() { [Attr.UserAccountControl] = value };
}
