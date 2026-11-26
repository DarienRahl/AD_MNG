using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers.Remote;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Feedback;
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
    public async Task TheReadsOnDemand_AreOperationsOfThePage_SaidInTheStatusLineOfTheSheet()
    {
        var (scenario, sheet) = await OpenAsync(s => s.Windows7);
        using var _sheet = sheet;
        var harness = scenario.Harness;
        var page = Status(sheet);
        sheet.SelectedPage = page;
        Assert.True(sheet.Status.Current.IsIdle);

        // A domain controller that cannot be reached: what was skipped, and what the page shows all the same.
        harness.ConnectToServer = (_, _, _) =>
            Task.FromException<Core.Directory.IDirectorySession>(new Core.Directory.DirectoryException(Core.Directory.DirectoryErrorKind.ServerUnavailable, "81: Server Down"));
        await page.ReadExactLastLogonCommand.ExecuteAsync(null);

        var partial = sheet.Status.Current;
        Assert.Equal(PageStatusKind.Attention, partial.Kind);
        Assert.Equal("Not every domain controller answered: dc02.contoso.test. The newest logon among the answers is shown.", partial.Message);
        Assert.Equal("Not every domain controller answered: dc02.contoso.test", page.Error);

        // The log copy has the number of servers, not their names.
        Assert.Equal("Computers.Status.ExactNotAnswered (1)", partial.Log.ToString());
        Assert.True(partial.CanClose);
        Assert.False(page.IsReading);
        Assert.False(page.Status.IsWorking);

        // A slow read says what it reads, with Stop - and Stop returns control.
        harness.Session.Latency = TimeSpan.FromSeconds(30);
        Task read;
        try
        {
            read = page.ReadExactLastLogonCommand.ExecuteAsync(null);
            Assert.True(page.IsReading);
            Assert.True(page.Status.IsWorking);
            Assert.False(sheet.Status.IsWorking);
            Assert.Equal("Reading the last logon on all domain controllers…", sheet.Status.Current.Message);
            Assert.Equal("Computers.Status.ReadingExact", sheet.Status.Current.Log.ToString());
            var stop = Assert.IsType<PageStatusAction>(sheet.Status.Current.Action);
            stop.Command.Execute(stop.Parameter);
            await read.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        finally
        {
            harness.Session.Latency = TimeSpan.Zero;
        }

        Assert.Equal(PageStatusKind.Attention, sheet.Status.Current.Kind);
        Assert.Equal(
            "Reading the last logon on all domain controllers was stopped. Press “Check on all DCs” to read it again.",
            sheet.Status.Current.Message);
        Assert.Equal("Computers.Status.ExactStopped", sheet.Status.Current.Log.ToString());
        Assert.False(page.IsReading);
        Assert.True(page.ReadExactLastLogonCommand.CanExecute(null));

        // The site cannot be determined: an error with the reason; nothing of it is the state of the page.
        var fail = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (fail && operation.Kind == FakeOperationKind.Search)
            {
                throw new Core.Directory.DirectoryException(Core.Directory.DirectoryErrorKind.AccessDenied, "50: Insufficient access") { LdapResultCode = 50 };
            }
        };
        await page.ResolveSiteCommand.ExecuteAsync(null);
        var reason = Assert.IsType<string>(page.Error);
        Assert.Equal(PageStatusKind.Error, sheet.Status.Current.Kind);
        Assert.Equal($"Could not determine the site of the computer: {reason} (LDAP 50)", sheet.Status.Current.Message);
        Assert.Equal("Computers.Status.SiteFailed [LDAP 50]", sheet.Status.Current.Log.ToString());
        Assert.True(sheet.Status.Current.CanClose);
        Assert.Null(page.Status.Lasting);

        // A read that goes through ends without a message: its result is the page.
        fail = false;
        scenario.Resolver.Add("pc-win7.contoso.test", "10.20.5.18");
        await page.ResolveSiteCommand.ExecuteAsync(null);
        Assert.Null(page.Error);
        Assert.True(sheet.Status.Current.IsIdle);
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
