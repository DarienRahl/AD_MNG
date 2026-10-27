using AdMng.Core.Computers;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;

namespace AdMng.Presentation.Tests.Computers;

/// <summary>The "Komputery" tab of the settings window (K1 design note 8).</summary>
public class ComputerSettingsTests
{
    private static async Task<ComputerScenario> ScenarioAsync(Action<ComputerSettings>? arrange = null, string culture = "pl-PL")
    {
        var scenario = await ComputerScenario.CreateAsync(culture);
        if (arrange is not null)
        {
            await scenario.Harness.Settings.UpdateAsync(s => arrange(s.Computers), TestContext.Current.CancellationToken);
        }

        return scenario;
    }

    [Fact]
    public async Task Loads_TheStoredValues()
    {
        var scenario = await ScenarioAsync(c =>
        {
            c.RemoteTimeoutSeconds = 45;
            c.RemoteUseHttps = true;
            c.RemotePort = 15986;
            c.RefreshUserPolicy = true;
            c.RestartDelayMinutes = 15;
            c.RestartMessage = "Zapisz pracę – serwis o 18:00. Łukasz Wiśniewski";
            c.Parameters = new ComputerReportParameters { InactiveDays = 60, EndingSoonDays = 30 };
            c.PlacementRules =
            [
                new ComputerPlacementSetting { Type = ComputerType.Workstation, Containers = ["OU=Stacje robocze,{domain}", "OU=Laptopy,OU=Kraków,{domain}"] },
                new ComputerPlacementSetting { Type = ComputerType.Server, Containers = ["OU=Serwery,{domain}"] },
            ];
            c.ReleaseOverrides = [new ComputerReleaseOverrideSetting { Build = 19045, Edition = EditionClass.EnterpriseEducation, EndOfSupport = new DateOnly(2028, 10, 10) }];
        });

        var tab = new ComputerSettingsViewModel(scenario.Services);

        Assert.Equal(45, tab.RemoteTimeoutSeconds);
        Assert.True(tab.RemoteUseHttps);
        Assert.Equal("15986", tab.RemotePortText);
        Assert.Equal("Puste: 5986", tab.PortHint);
        Assert.True(tab.RefreshUserPolicy);
        Assert.Equal(15, tab.RestartDelayMinutes);
        Assert.Equal("Zapisz pracę – serwis o 18:00. Łukasz Wiśniewski", tab.RestartMessage);
        Assert.Equal(30, tab.EndingSoonDays);
        Assert.Equal("OU=Stacje robocze,{domain}" + Environment.NewLine + "OU=Laptopy,OU=Kraków,{domain}", tab.WorkstationContainers);
        Assert.Equal("OU=Serwery,{domain}", tab.ServerContainers);
        var row = Assert.Single(tab.Overrides);
        Assert.Equal((19045, EditionClass.EnterpriseEducation, new DateOnly(2028, 10, 10)), (row.Build, row.Edition, row.EndOfSupport));
        Assert.StartsWith("19045 – ", row.Text, StringComparison.Ordinal);
        Assert.Contains("10.10.2028", row.Text, StringComparison.Ordinal);
        Assert.Null(tab.Error);
    }

    [Fact]
    public async Task Apply_WritesTheTab_KeepsOtherSettings()
    {
        var scenario = await ScenarioAsync(c =>
        {
            c.Parameters = new ComputerReportParameters { InactiveDays = 60, EndingSoonDays = 90 };
            c.DisabledReports = [ComputerReport.Disabled];
            c.PlacementRules = [new ComputerPlacementSetting { Type = ComputerType.Unknown, Containers = ["OU=Nieznane,{domain}"] }];
        });
        var tab = new ComputerSettingsViewModel(scenario.Services)
        {
            RemoteTimeoutSeconds = 60,
            RemotePortText = " 5999 ",
            RestartDelayMinutes = 0,
            RestartMessage = "   ",
            EndingSoonDays = 14,
            WorkstationContainers = "OU=Stacje robocze,{domain}\r\n\r\n ou=stacje robocze,{domain} \nOU=Księgowość,OU=Warszawa,{domain}",
            ServerContainers = string.Empty,
        };

        await scenario.Harness.Settings.UpdateAsync(tab.Apply, TestContext.Current.CancellationToken);

        var stored = scenario.Harness.Settings.Current.Computers;
        Assert.Equal(60, stored.RemoteTimeoutSeconds);
        Assert.Equal(5999, stored.RemotePort);
        Assert.Equal(0, stored.RestartDelayMinutes);
        Assert.Null(stored.RestartMessage);
        Assert.Equal(new ComputerReportParameters { InactiveDays = 60, EndingSoonDays = 14 }, stored.Parameters);
        Assert.Equal([ComputerReport.Disabled], stored.DisabledReports);
        Assert.Equal(2, stored.PlacementRules.Count);
        var workstations = Assert.Single(stored.PlacementRules, r => r.Type == ComputerType.Workstation);
        Assert.Equal(["OU=Stacje robocze,{domain}", "OU=Księgowość,OU=Warszawa,{domain}"], workstations.Containers);
        Assert.Contains(stored.PlacementRules, r => r.Type == ComputerType.Unknown);
        Assert.DoesNotContain(stored.PlacementRules, r => r.Type == ComputerType.Server);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("port")]
    [InlineData("delay")]
    [InlineData("message")]
    [InlineData("container")]
    public async Task InvalidValues_GiveAnError(string what)
    {
        var scenario = await ScenarioAsync();
        var tab = new ComputerSettingsViewModel(scenario.Services);
        Assert.Null(tab.Error);

        switch (what)
        {
            case "timeout":
                tab.RemoteTimeoutSeconds = 2;
                break;
            case "port":
                tab.RemotePortText = "70000";
                break;
            case "delay":
                tab.RestartDelayMinutes = ComputerSettings.MaxRestartDelayMinutes + 1;
                break;
            case "message":
                tab.RestartMessage = new string('ł', ComputerSettings.MaxRestartMessageLength + 1);
                break;
            default:
                tab.ServerContainers = "OU=Serwery,{domain}" + Environment.NewLine + "Serwery bez nazwy wyróżniającej";
                break;
        }

        Assert.NotNull(tab.Error);
        if (what == "container")
        {
            Assert.Equal("Serwery: „Serwery bez nazwy wyróżniającej” nie jest poprawną nazwą wyróżniającą.", tab.Error);
        }
    }

    [Fact]
    public async Task Overrides_AreAddedSortedReplacedAndRemoved()
    {
        var scenario = await ScenarioAsync(culture: "en-US");
        var tab = new ComputerSettingsViewModel(scenario.Services);
        Assert.False(tab.HasOverrides);
        Assert.False(tab.AddOverrideCommand.CanExecute(null));

        tab.NewBuild = "abc";
        Assert.Equal("The build is a positive number, e.g. 19045.", tab.OverrideError);

        tab.NewBuild = "26100";
        tab.NewEdition = tab.Editions.Single(e => e.Value == EditionClass.HomePro);
        tab.NewEndOfSupport = new DateTime(2027, 1, 1);
        tab.AddOverrideCommand.Execute(null);
        tab.NewBuild = "19045";
        tab.NewEndOfSupport = new DateTime(2026, 10, 13);
        tab.AddOverrideCommand.Execute(null);
        tab.NewBuild = "26100";
        tab.NewEndOfSupport = new DateTime(2027, 6, 30);
        tab.AddOverrideCommand.Execute(null);

        Assert.True(tab.HasOverrides);
        Assert.Equal([(19045, new DateOnly(2026, 10, 13)), (26100, new DateOnly(2027, 6, 30))], tab.Overrides.Select(o => (o.Build, o.EndOfSupport)));
        Assert.Equal(string.Empty, tab.NewBuild);
        Assert.Null(tab.NewEndOfSupport);

        tab.RemoveOverrideCommand.Execute(tab.Overrides[0]);
        await scenario.Harness.Settings.UpdateAsync(tab.Apply, TestContext.Current.CancellationToken);

        var stored = Assert.Single(scenario.Harness.Settings.Current.Computers.ReleaseOverrides);
        Assert.Equal((26100, EditionClass.HomePro, new DateOnly(2027, 6, 30)), (stored.Build, stored.Edition, stored.EndOfSupport));
    }

    [Fact]
    public async Task SettingsWindow_SavesTheComputersTab_AndAnErrorBlocksSaving()
    {
        var scenario = await ScenarioAsync();
        using var settings = new SettingsViewModel(scenario.Services, new FakeThemeService());

        settings.Computers.RemotePortText = "port";
        Assert.NotNull(settings.Error);
        Assert.False(settings.SaveCommand.CanExecute(null));

        settings.Computers.RemotePortText = string.Empty;
        settings.Computers.RefreshUserPolicy = true;
        settings.Computers.ServerContainers = "OU=Serwery,OU=Gdańsk,{domain}";
        Assert.True(settings.SaveCommand.CanExecute(null));
        await settings.SaveCommand.ExecuteAsync(null);

        var stored = scenario.Harness.Settings.Current.Computers;
        Assert.True(stored.RefreshUserPolicy);
        Assert.Null(stored.RemotePort);
        Assert.Equal(["OU=Serwery,OU=Gdańsk,{domain}"], Assert.Single(stored.PlacementRules).Containers);
    }
}
