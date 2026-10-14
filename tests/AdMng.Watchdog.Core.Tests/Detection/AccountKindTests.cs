using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>Account kinds (200–211) end to end through the fake directory.</summary>
public class AccountKindTests
{
    private const int Normal = (int)UserAccountControl.NormalAccount;

    [Fact]
    public async Task AccountDisabled_AndEnabledAfterLongDisable()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.UserAccountControl, Normal | (int)UserAccountControl.AccountDisable);
        await scenario.ReplaceAsync(scenario.Company.ZbigniewKazmierczak, Attr.UserAccountControl, Normal);
        var events = await scenario.CycleEventsAsync();

        var disabled = Assert.Single(events, e => e.Kind == WatchEventKind.AccountDisabled);
        Assert.Equal(scenario.Company.JanKowalski, disabled.Object.Dn);
        Assert.Equal(Severity.Info, disabled.Severity);
        var enabled = Assert.Single(events, e => e.Kind == WatchEventKind.AccountEnabled);
        Assert.Equal(scenario.Company.ZbigniewKazmierczak, enabled.Object.Dn);
        Assert.Contains(EventTags.LongDisabled, enabled.Tags);
        Assert.Equal(Severity.High, enabled.Severity);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task AccountEnabled_ShortlyAfterUseIsLow()
    {
        await using var scenario = new DetectionScenario();
        scenario.Directory.SimulateSuccessfulLogon(scenario.Company.JanKowalski);
        await scenario.BaselineAsync();
        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.UserAccountControl, Normal | (int)UserAccountControl.AccountDisable);
        await scenario.CycleEventsAsync();

        scenario.Advance(TimeSpan.FromHours(2));
        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.UserAccountControl, Normal);
        var enabled = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.AccountEnabled, enabled.Kind);
        Assert.DoesNotContain(EventTags.LongDisabled, enabled.Tags);
        Assert.Equal(Severity.Low, enabled.Severity);
    }

    [Fact]
    public async Task AccountLockedOut_CarriesTheEffectiveDomainLockoutAndCountsLockoutsToday()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        scenario.Directory.SimulateLockout(scenario.Company.PawelLewandowski);
        var first = Assert.Single(await scenario.CycleEventsAsync());
        scenario.Advance(TimeSpan.FromMinutes(45));
        scenario.Directory.SimulateLockout(scenario.Company.PawelLewandowski);
        var second = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.AccountLockedOut, first.Kind);
        Assert.Equal(AttributionStatus.NotApplicable, first.Attribution.Status);
        var lockout = Assert.IsType<LockoutInfo>(first.Lockout);
        Assert.Equal(DetectionScenario.Start, lockout.LockedAt);
        Assert.Equal(DetectionScenario.Start.AddMinutes(30), lockout.LockedUntil);
        Assert.Equal(1, lockout.LockoutsToday);
        Assert.Equal(2, second.Lockout!.LockoutsToday);
    }

    [Fact]
    public async Task AccountLockedOut_UsesTheResultantPsoLockoutDuration()
    {
        string? pso = null;
        await using var scenario = new DetectionScenario(seed: b => pso = b.AddPasswordSettings("PSO-Handlowcy", 10, TimeSpan.FromHours(2)));
        scenario.Directory.SetResultantPso(scenario.Company.ZanetaSzymanska, pso);
        await scenario.BaselineAsync();

        scenario.Directory.SimulateLockout(scenario.Company.ZanetaSzymanska);
        var locked = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(DetectionScenario.Start.AddHours(2), locked.Lockout!.LockedUntil);
    }

    [Fact]
    public async Task AccountUnlocked_BeforeTheLockoutExpired()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        scenario.Directory.SimulateLockout(scenario.Company.PawelLewandowski);
        await scenario.CycleEventsAsync();

        scenario.Advance(TimeSpan.FromMinutes(5));
        await scenario.ReplaceAsync(scenario.Company.PawelLewandowski, Attr.LockoutTime, "0");
        var unlocked = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.AccountUnlocked, unlocked.Kind);
        Assert.Equal(Severity.Info, unlocked.Severity);
    }

    [Fact]
    public async Task LockoutTimeResetAtLogonAfterExpiry_IsNoEvent()
    {
        await using var scenario = new DetectionScenario();
        scenario.Directory.LockoutDuration = TimeSpan.FromMinutes(30);
        await scenario.BaselineAsync();
        scenario.Directory.SimulateLockout(scenario.Company.PawelLewandowski);
        await scenario.CycleEventsAsync();

        scenario.Advance(TimeSpan.FromMinutes(31));
        scenario.Directory.SimulateSuccessfulLogon(scenario.Company.PawelLewandowski);
        var events = await scenario.CycleEventsAsync();

        Assert.Empty(events);
        Assert.Equal(["0"], scenario.Directory.GetValues(scenario.Company.PawelLewandowski, Attr.LockoutTime));
    }

    [Fact]
    public async Task PasswordChanged_ForUsers_AndComputersOnlyWhenTracked()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        scenario.Advance(TimeSpan.FromMinutes(1));

        await scenario.ResetPasswordAsync(scenario.Company.MalgorzataWojcik);
        await scenario.ResetPasswordAsync(scenario.Company.Computer1);
        var events = await scenario.CycleEventsAsync();

        var changed = Assert.Single(events);
        Assert.Equal(WatchEventKind.PasswordChanged, changed.Kind);
        Assert.Equal(scenario.Company.MalgorzataWojcik, changed.Object.Dn);
        Assert.Equal(AttributionStatus.Pending, changed.Attribution.Status);

        scenario.Config.Update(c => c with { Directory = c.Directory with { TrackComputerPasswordChanges = true } });
        scenario.Advance(TimeSpan.FromMinutes(1));
        await scenario.ResetPasswordAsync(scenario.Company.Computer1);
        var computer = Assert.Single(await scenario.CycleEventsAsync());
        Assert.Equal(WatchEventKind.PasswordChanged, computer.Kind);
        Assert.Equal(WatchObjectKind.Computer, computer.Object.Kind);
    }

    [Fact]
    public async Task ManagedPasswordRollover_IsDroppedByDefault()
    {
        string? gmsa = null;
        await using var scenario = new DetectionScenario(seed: b => gmsa = b.AddServiceAccount("gmsa-web01"));
        await scenario.BaselineAsync();
        scenario.Advance(TimeSpan.FromMinutes(1));

        await scenario.ResetPasswordAsync(gmsa!);

        Assert.Empty(await scenario.CycleEventsAsync());
    }

    [Fact]
    public async Task PasswordMustChangeSet_ThenPasswordAgeReset()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();
        scenario.Advance(TimeSpan.FromMinutes(1));

        await scenario.ReplaceAsync(scenario.Company.LukaszWisniewski, Attr.PwdLastSet, "0");
        var mustChange = Assert.Single(await scenario.CycleEventsAsync());
        scenario.Advance(TimeSpan.FromMinutes(1));
        await scenario.ReplaceAsync(scenario.Company.LukaszWisniewski, Attr.PwdLastSet, "-1");
        var ageReset = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.PasswordMustChangeSet, mustChange.Kind);
        Assert.Equal(WatchEventKind.PasswordAgeReset, ageReset.Kind);
        Assert.Equal(Severity.Medium, ageReset.Severity);
    }

    [Fact]
    public async Task AccountFlagsChanged_SeverityIsTheMaximumOfTheBits()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.PawelLewandowski, Attr.UserAccountControl, Normal | (int)UserAccountControl.DontExpirePassword);
        await scenario.ReplaceAsync(scenario.Company.BartlomiejKozlowski, Attr.UserAccountControl,
            Normal | (int)UserAccountControl.DontExpirePassword | (int)UserAccountControl.DontRequirePreauth);
        var events = await scenario.CycleEventsAsync();

        var medium = Assert.Single(events, e => e.Object.Dn == scenario.Company.PawelLewandowski);
        Assert.Equal(WatchEventKind.AccountFlagsChanged, medium.Kind);
        Assert.Equal(Severity.Medium, medium.Severity);
        Assert.Equal(["DONT_EXPIRE_PASSWORD"], Assert.Single(medium.Changes).FlagsAdded);
        var high = Assert.Single(events, e => e.Object.Dn == scenario.Company.BartlomiejKozlowski);
        Assert.Equal(Severity.High, high.Severity);
    }

    [Fact]
    public async Task AccountExpirationChanged_IsMediumWhenClearedToNever()
    {
        await using var scenario = new DetectionScenario();
        var expires = AdConvert.ToFileTime(new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero)).ToString(CultureInfo.InvariantCulture);
        scenario.Directory.SeedAttributes(scenario.Company.GrazynaDabrowska, new Dictionary<string, object?> { [Attr.AccountExpires] = expires });
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.LukaszWisniewski, Attr.AccountExpires, expires);
        await scenario.ReplaceAsync(scenario.Company.GrazynaDabrowska, Attr.AccountExpires, AdConvert.FileTimeNever.ToString(CultureInfo.InvariantCulture));
        var events = await scenario.CycleEventsAsync();

        Assert.Equal(Severity.Info, Assert.Single(events, e => e.Object.Dn == scenario.Company.LukaszWisniewski).Severity);
        var never = Assert.Single(events, e => e.Object.Dn == scenario.Company.GrazynaDabrowska);
        Assert.Equal(WatchEventKind.AccountExpirationChanged, never.Kind);
        Assert.Equal(Severity.Medium, never.Severity);
    }

    [Fact]
    public async Task AccountNameChanged_ForSamAccountNameAndUpn()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.MalgorzataWojcik, Attr.SamAccountName, "m.wojcik-nowak");
        var renamed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.AccountNameChanged, renamed.Kind);
        Assert.Equal(Severity.Medium, renamed.Severity);
        Assert.Equal(["m.wojcik-nowak"], Assert.Single(renamed.Changes).NewValues);
    }

    [Fact]
    public async Task DisabledKinds_AreNeverRecorded()
    {
        await using var scenario = new DetectionScenario(configuration: new WatchdogConfiguration
        {
            Directory = new DirectorySettings { DisabledKinds = [WatchEventKind.AttributesChanged] },
        });
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.JanKowalski, Attr.Title, "Architekt");

        Assert.Empty(await scenario.CycleEventsAsync());
        Assert.Equal("Architekt", scenario.Snapshot(scenario.Company.JanKowalski)!.Attributes[Attr.Title].Values[0]);
    }
}
