using AdMng.Core.ActiveDirectory;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>Tags (section 10.6.2) and their severity bumps, end to end.</summary>
public class EventTaggerScenarioTests
{
    [Fact]
    public async Task RedirectedComputersContainer_TagsDefaultAndUnexpectedContainers()
    {
        await using var scenario = new DetectionScenario();
        scenario.Directory.SeedAttributes(scenario.Builder.DomainDn, new Dictionary<string, object?>
        {
            ["wellKnownObjects"] = new[]
            {
                $"B:32:{FakeDirectoryBuilder.UsersContainerWellKnownGuid}:{scenario.Builder.UsersDn}",
                $"B:32:{FakeDirectoryBuilder.ComputersContainerWellKnownGuid}:{scenario.Company.ComputersOu}",
            },
        });
        await scenario.BaselineAsync();

        var literal = scenario.Builder.AddComputer("WAW-PC100", scenario.Builder.ComputersDn);
        var redirected = scenario.Builder.AddComputer("WAW-PC101", scenario.Company.ComputersOu);
        var events = await scenario.SettleAsync();

        var unexpected = Assert.Single(events, e => e.Object.Dn == literal);
        Assert.Contains(EventTags.UnexpectedContainer, unexpected.Tags);
        Assert.Equal(Severity.Medium, unexpected.Severity);
        var normal = Assert.Single(events, e => e.Object.Dn == redirected);
        Assert.Contains(EventTags.DefaultContainer, normal.Tags);
        Assert.Equal(Severity.Info, normal.Severity);
    }

    [Fact]
    public async Task ComputerCreatedByANonAdmin_IsMedium()
    {
        await using var scenario = new DetectionScenario();
        await scenario.BaselineAsync();

        scenario.Builder.AddComputer("PRYWATNY-LAPTOP", scenario.Builder.ComputersDn, new Dictionary<string, object?>
        {
            [WatchdogAttributes.CreatorSid] = $"{scenario.Builder.DomainSid}-1110",
        });
        var created = Assert.Single(await scenario.SettleAsync());

        Assert.Contains(EventTags.CreatedByNonAdmin, created.Tags);
        Assert.Equal(Severity.Medium, created.Severity);
    }

    [Fact]
    public async Task AdminCountOrphan_IsTaggedWithoutABump()
    {
        await using var scenario = new DetectionScenario();
        scenario.Directory.SeedAttributes(scenario.Company.GrazynaDabrowska, new Dictionary<string, object?> { [Attr.AdminCount] = 1 });
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.GrazynaDabrowska, Attr.Title, "Starsza księgowa");
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Contains(EventTags.AdminCountOrphan, changed.Tags);
        Assert.DoesNotContain(EventTags.Privileged, changed.Tags);
        Assert.Equal(Severity.Info, changed.Severity);
    }

    [Fact]
    public async Task ServiceAccountTag_BumpsDisableToHigh()
    {
        await using var scenario = new DetectionScenario(configuration: new WatchdogConfiguration
        {
            Directory = new DirectorySettings { ServiceAccountSubtrees = ["OU=Sprzedaż,OU=Pracownicy,OU=Firma,{domain}"] },
        });
        await scenario.BaselineAsync();

        await scenario.ReplaceAsync(scenario.Company.BartlomiejKozlowski, Attr.UserAccountControl,
            (int)(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable));
        var disabled = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Contains(EventTags.ServiceAccount, disabled.Tags);
        Assert.Equal(Severity.High, disabled.Severity);
    }

    [Fact]
    public async Task MoveOutOfADisabledAccountsOu_IsHigh()
    {
        string? disabledOu = null;
        await using var scenario = new DetectionScenario(seed: b => disabledOu = b.AddOu("Wyłączone", b.DomainDn));
        scenario.Config.Update(c => c with { Directory = c.Directory with { DisabledAccountsSubtrees = [disabledOu!] } });
        await scenario.BaselineAsync();
        var parked = await scenario.MoveAsync(scenario.Company.ZbigniewKazmierczak, disabledOu);
        Assert.Equal(Severity.Info, Assert.Single(await scenario.CycleEventsAsync()).Severity);

        await scenario.MoveAsync(parked, scenario.Company.AccountingOu);
        var back = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.ObjectMoved, back.Kind);
        Assert.Contains(EventTags.DisabledOu, back.Tags);
        Assert.Equal(Severity.High, back.Severity);
    }

    [Fact]
    public async Task KrbtgtPasswordChange_IsExactlyMedium()
    {
        await using var scenario = new DetectionScenario();
        await scenario.AddValuesAsync(scenario.Builder.DomainAdminsDn, Attr.Member, scenario.Builder.KrbtgtDn);
        await scenario.BaselineAsync();
        scenario.Advance(TimeSpan.FromMinutes(1));

        await scenario.ResetPasswordAsync(scenario.Builder.KrbtgtDn);
        var changed = Assert.Single(await scenario.CycleEventsAsync());

        Assert.Equal(WatchEventKind.PasswordChanged, changed.Kind);
        Assert.Contains(EventTags.Krbtgt, changed.Tags);
        Assert.Equal(Severity.Medium, changed.Severity);
    }
}
