using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.IntegrationTests.Infrastructure;

namespace AdMng.Watchdog.IntegrationTests.Detection;

/// <summary>
/// The creation settle window (spec 10.6.3) against real multi-step account creation: the account is added disabled,
/// gets a password and is enabled in separate detection cycles; with a 2-minute window on the engine's fake clock the
/// three steps are one UserCreated with the final state, emitted once the window is over.
/// </summary>
public sealed class CreationSettleTests(ReadOnlyEngineFixture engine, LabFixture lab) : IClassFixture<ReadOnlyEngineFixture>
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EngineHost Host => engine.Host;

    [Fact]
    public async Task MultiStepCreation_InsideTheWindow_IsOneUserCreatedWithTheFinalState()
    {
        WatchdogLab.RequireLab();
        await Host.SaveConfigurationAsync(c => c with { Directory = c.Directory with { CreationSettleWindow = Window } }, Ct);
        var ou = await lab.CreateTestOuAsync("Wieloetapowe", Ct);
        var mark = await Host.MarkAsync(Ct);

        var user = await lab.Writer.CreateUserAsync(ou, "Łucja", "Ćwik", password: null, new Dictionary<string, string>
        {
            [Attr.Department] = "Kadry i płace",
        }, Ct);
        await Host.RunDetectionAsync(Ct);
        var password = TestNames.Password();
        await lab.Writer.ResetPasswordAsync(user.Dn, password, Ct);
        await Host.RunDetectionAsync(Ct);
        await lab.Writer.SetAccountControlAsync(user.Dn, UserAccountControl.NormalAccount, Ct);
        await Host.RunDetectionAsync(Ct);
        EventAssert.None(await Host.EventsAsync(ou, mark, Ct), user.Dn);

        Host.Clock.Advance(Window + TimeSpan.FromSeconds(30));
        await Host.RunDetectionAsync(Ct);

        var events = await Host.EventsAsync(ou, mark, Ct);
        var created = EventAssert.Single(events, WatchEventKind.UserCreated, user.Dn);
        Assert.True(EventAssert.Of(events, user.Dn).Count() == 1, $"one event for the three steps: {EventAssert.Describe(events)}");
        var uac = Assert.Single(EventAssert.Change(created, Attr.UserAccountControl).NewValues);
        if (int.TryParse(uac, NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags))
        {
            Assert.Equal(0, flags & (int)UserAccountControl.AccountDisable);
        }
        else
        {
            Assert.DoesNotContain("ACCOUNTDISABLE", uac, StringComparison.Ordinal);
        }

        Assert.Equal(["Kadry i płace"], EventAssert.Change(created, Attr.Department).NewValues);
    }
}
