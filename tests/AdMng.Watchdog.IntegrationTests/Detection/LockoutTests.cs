using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.IntegrationTests.Infrastructure;

namespace AdMng.Watchdog.IntegrationTests.Detection;

/// <summary>
/// Lockouts from real failed simple binds (lab: lockoutThreshold 5, lockoutDuration 30 min) and the logon noise that
/// must stay silent (spec 22.4, Appendix B): a failed bind changes badPwdCount without a USN bump, the first successful
/// bind writes lastLogonTimestamp (a USN bump of a noise attribute).
/// </summary>
public sealed class LockoutTests(ReadOnlyEngineFixture engine, LabFixture lab) : IClassFixture<ReadOnlyEngineFixture>
{
    private const int LockoutThreshold = 5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EngineHost Host => engine.Host;

    [Fact]
    public async Task FiveBadBinds_AreAccountLockedOut_AndTheAdminUnlockIsAccountUnlocked()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Blokady", Ct);
        var user = await lab.Writer.CreateUserAsync(ou, "Paweł", "Lewandowski", TestNames.Password(), cancellationToken: Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        for (var i = 0; i < LockoutThreshold; i++)
        {
            await LabWriter.BadBindAsync(user, Ct);
        }

        var lockedAt = DateTimeOffset.UtcNow;
        await Host.RunDetectionAsync(Ct);

        var locked = EventAssert.Single(await Host.EventsAsync(ou, mark, Ct), WatchEventKind.AccountLockedOut, user.Dn);
        var lockout = Assert.IsType<LockoutInfo>(locked.Lockout);
        Assert.InRange(lockout.LockedAt, lockedAt.AddMinutes(-2), lockedAt.AddMinutes(1));
        var until = Assert.IsType<DateTimeOffset>(lockout.LockedUntil);
        Assert.InRange(until - lockout.LockedAt, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
        Assert.Equal(DetectionSource.IncrementalPoll, locked.Source);

        mark = await Host.MarkAsync(Ct);
        await lab.Writer.ReplaceAsync(user.Dn, Attr.LockoutTime, "0", Ct);
        await Host.RunDetectionAsync(Ct);

        EventAssert.Single(await Host.EventsAsync(ou, mark, Ct), WatchEventKind.AccountUnlocked, user.Dn);
    }

    [Fact]
    public async Task OneBadBind_IsNoEvent()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Błędne hasło", Ct);
        var user = await lab.Writer.CreateUserAsync(ou, "Katarzyna", "Mazur", TestNames.Password(), cancellationToken: Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        await LabWriter.BadBindAsync(user, Ct);
        await Host.RunDetectionAsync(Ct);

        EventAssert.None(await Host.EventsAsync(ou, mark, Ct), user.Dn);
    }

    [Fact]
    public async Task SuccessfulBind_IsNoEvent()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Logowanie", Ct);
        var user = await lab.Writer.CreateUserAsync(ou, "Tomasz", "Jankowski", TestNames.Password(), cancellationToken: Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        // The first successful bind writes lastLogonTimestamp – it bumps the USN but is noise.
        await LabWriter.BindAsync(user, Ct);
        var after = await lab.Admin.GetAsync(user.Dn, [Attr.LastLogonTimestamp], Ct);
        Assert.NotNull(after?.GetString(Attr.LastLogonTimestamp));
        await Host.RunDetectionAsync(Ct);

        EventAssert.None(await Host.EventsAsync(ou, mark, Ct), user.Dn);
    }
}
