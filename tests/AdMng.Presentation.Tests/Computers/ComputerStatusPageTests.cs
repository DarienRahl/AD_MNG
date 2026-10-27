using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers.Remote;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Tests.AccountReview;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Computers;

/// <summary>The computer pages of the property sheet (K1.4): type, release and support, and the "Stan" page with its buttons.</summary>
public class ComputerStatusPageTests
{
    private static async Task<(ComputerScenario Scenario, PropertySheetViewModel Sheet)> OpenAsync(Func<ComputerInventorySample, string> computer, string culture = "en-US")
    {
        var scenario = await ComputerScenario.CreateAsync(culture);
        var harness = scenario.Harness;
        var sheet = new PropertySheetViewModel(scenario.Services, harness.Planner, computer(scenario.Sample))
        {
            DomainControllers = new DomainControllerReader(new ServerConnector(harness), harness.Errors),
        };
        await sheet.InitializeAsync();
        return (scenario, sheet);
    }

    private static string Value(PropertySheetViewModel sheet, string page, string label) =>
        Assert.Single(Assert.IsType<FieldsPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == page)).Fields.OfType<FieldViewModel>(), f => f.Label == label).Value;

    private static ComputerStatusPageViewModel Status(PropertySheetViewModel sheet) => Assert.IsType<ComputerStatusPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == L.Computers.StatusPage));

    private static string Detail(ComputerStatusPageViewModel page, string label) => Assert.Single(page.Details, d => d.Label == label).Value;

    [Fact]
    public async Task GeneralAndOperatingSystem_ShowTypeReleaseAndSupport()
    {
        var (_, sheet) = await OpenAsync(s => s.SqlServer);
        using var _sheet = sheet;

        Assert.Equal("Server", Value(sheet, L.Pages.General, "Type"));
        Assert.Equal("Windows Server 2016", Value(sheet, L.Pages.OperatingSystem, "Release"));
        Assert.Equal("ends 1/12/2027 (in 77 days)", Value(sheet, L.Pages.OperatingSystem, "Support"));
    }

    [Fact]
    public async Task StatusPage_ShowsTheFacts_CreatorAndObjectOwner()
    {
        var (scenario, sheet) = await OpenAsync(s => s.Prestaged, "pl-PL");
        using var _sheet = sheet;
        var page = Status(sheet);

        Assert.Equal("Stan", page.Title);
        Assert.Equal("Nieznany (brak systemu operacyjnego)", Detail(page, "Typ"));
        Assert.Equal(@"CONTOSO\lukasz.wisniewski", Detail(page, "Dołączył (twórca)"));
        Assert.Contains(page.Details, d => d.Label == "Właściciel obiektu");
        Assert.Equal("jeszcze nie odczytano", Detail(page, "Dokładne ostatnie logowanie"));
        Assert.Equal("jeszcze nie odczytano", Detail(page, "Lokacja (podsieć)"));
        Assert.True(page.CanUseRemote);
        Assert.False(page.HasChanges);
        Assert.Empty(scenario.Harness.Dialogs.Errors);
    }

    [Fact]
    public async Task CheckOnAllDomainControllers_TakesTheNewestLastLogon()
    {
        var (scenario, sheet) = await OpenAsync(s => s.Windows7);
        using var _sheet = sheet;
        var harness = scenario.Harness;
        harness.ConnectToServer = (current, server, _) =>
        {
            var other = new HookedSession(new FakeDomainControllerSession(harness.Session, server));
            other.Extra[scenario.Sample.Windows7] = (Attr.LastLogon, AdConvert.ToFileTime(ComputerInventorySample.Now.AddDays(-1)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult<Core.Directory.IDirectorySession>(other);
        };
        var page = Status(sheet);

        await page.ReadExactLastLogonCommand.ExecuteAsync(null);

        Assert.Null(page.Error);
        Assert.StartsWith(new DateTime(2026, 10, 26, 9, 0, 0).ToString("g", System.Globalization.CultureInfo.GetCultureInfo("en-US")), Detail(page, "Exact last logon"), StringComparison.Ordinal);
        Assert.Contains("dc02.contoso.test", Detail(page, "Exact last logon"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveSite_UsesTheResolverAndTheSubnets()
    {
        var (scenario, sheet) = await OpenAsync(s => s.Pro24H2);
        using var _sheet = sheet;
        scenario.Resolver.Add("krk-pc101.contoso.test", "10.20.5.17");
        var page = Status(sheet);
        Assert.True(page.CanResolveSite);

        await page.ResolveSiteCommand.ExecuteAsync(null);

        Assert.Equal("Kraków (10.20.5.0/24)", Detail(page, "Site (subnet)"));
        Assert.Equal(["krk-pc101.contoso.test"], scenario.Resolver.Lookups);
    }

    [Fact]
    public async Task Buttons_RunTheComputerActions()
    {
        var (scenario, sheet) = await OpenAsync(s => s.FileServer);
        using var _sheet = sheet;
        scenario.Ping.Answering["srv-pliki01.contoso.test"] = TimeSpan.FromMilliseconds(1);
        var page = Status(sheet);

        await page.RemoteActionsCommand.ExecuteAsync(null);
        await page.RemoteDesktopCommand.ExecuteAsync(null);
        await page.ComputerManagementCommand.ExecuteAsync(null);
        await page.EventViewerCommand.ExecuteAsync(null);
        await page.PingCommand.ExecuteAsync(null);

        Assert.Equal([scenario.Sample.FileServer], scenario.Dialogs.RemoteWindows);
        Assert.Equal([ComputerAction.RemoteDesktop, ComputerAction.ComputerManagement, ComputerAction.EventViewer, ComputerAction.Ping], scenario.Dialogs.ActionsRun.Select(a => a.Action));
        Assert.Equal([LocalTool.RemoteDesktop, LocalTool.ComputerManagement, LocalTool.EventViewer], scenario.Launcher.Launches.Select(l => l.Tool));
        Assert.Equal(["srv-pliki01.contoso.test"], scenario.Ping.Pinged);
    }

    [Fact]
    public async Task WithoutTheWindowsHost_TheButtonsAreNotOffered()
    {
        var scenario = await ComputerScenario.CreateAsync();
        using var sheet = scenario.Harness.Factory.CreatePropertySheet(scenario.Sample.FileServer);
        await sheet.InitializeAsync();
        var page = Status(sheet);

        Assert.False(page.CanUseRemote);
        Assert.False(page.CanResolveSite);
        Assert.False(page.ResolveSiteCommand.CanExecute(null));
    }
}
