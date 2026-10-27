using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Tests.Dialogs;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>The Group Policy documents (design note docs/dev/k-computers-and-gpo.md 5.2) on the Polish GPO sample.</summary>
public sealed class GroupPolicyDocumentTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task Objects_ListEveryGpo_WithStatusLinksVersionsAndOwner()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.ObjectsAsync();

        Assert.Null(document.Error);
        Assert.Null(document.SysvolWarning);
        Assert.Equal("Obiekty zasad grupy", document.Title);
        Assert.Equal(7, document.Rows.Count);
        var passwords = document.Rows.Single(r => r.Gpo.Id == h.Sample.WorkstationPasswords);
        Assert.Equal("Stacje robocze – Zasady haseł", passwords.Name);
        Assert.Equal("Tylko stacje robocze", passwords.WmiFilterText);
        Assert.Equal("Komputery", passwords.LinksText);
        Assert.Equal("komputer 4, użytkownik 0", passwords.DirectoryVersionText);
        Assert.Equal("komputer 4, użytkownik 0", passwords.SysvolVersionText);
        Assert.False(passwords.HasVersionMismatch);

        var servers = document.Rows.Single(r => r.Gpo.Id == h.Sample.ServerSecurity);
        Assert.Equal("Ustawienia konfiguracji użytkownika wyłączone", servers.StatusText);
        Assert.Equal(h.B2.Builder.DomainDnsName, servers.LinksText);

        // GPT.INI behind AD: SYSVOL replication pending on this DC - marked.
        var drives = document.Rows.Single(r => r.Gpo.Id == h.Sample.DriveMappings);
        Assert.True(drives.HasVersionMismatch);
        Assert.Equal("komputer 0, użytkownik 2", drives.DirectoryVersionText);
        Assert.Equal("komputer 0, użytkownik 1", drives.SysvolVersionText);
        Assert.Contains("1", document.StatusText, StringComparison.Ordinal);

        var site = document.Rows.Single(r => r.Gpo.Id == h.Sample.SitePrinters);
        Assert.Equal("Warszawa", site.LinksText);
        Assert.All(document.Rows, r => Assert.NotEqual("–", r.OwnerText));
        Assert.Equal([h.B2.Session.Info.Server], h.Sysvol.OpenedOn.Distinct());
    }

    [Fact]
    public async Task Gpo_ShowsScopeDetailsSettingsDelegationAndStatus()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.WorkstationRemoteDesktop);

        Assert.Equal("Stacje robocze – Pulpit zdalny", document.Title);
        var link = Assert.Single(document.Links);
        Assert.Equal("Komputery", link.ContainerName);
        Assert.False(link.Enabled);
        Assert.Equal("Nie", link.EnabledText);
        Assert.Equal("contoso.test/Firma/Komputery", link.PathText);

        // Security filtering: GG_IT only (Authenticated Users only read).
        var filter = Assert.Single(document.SecurityFilter);
        Assert.Equal("CONTOSO\\GG_IT", filter.Name);
        Assert.Contains(document.Delegation, r => r.Name == "Użytkownicy uwierzytelnieni" && r.Level == GpoPermissionLevel.Read);
        Assert.Equal("<brak>", document.SelectedWmiFilter?.Text);

        Assert.Equal(h.B2.Builder.DomainDnsName, document.DomainText);
        Assert.Equal("AD: 2, SYSVOL: 2", document.ComputerVersionText);
        Assert.Equal(GpoStatus.AllSettingsEnabled, document.SelectedStatus?.Value);
        Assert.Equal(GroupPolicyText.Guid(h.Sample.WorkstationRemoteDesktop), document.GuidText);

        Assert.Contains(document.Settings, s => s.PathText.StartsWith("Komputer", StringComparison.Ordinal) && s.Name.Contains("Group Membership", StringComparison.Ordinal));
        Assert.Contains(document.Checks, c => c.Text == "Folder GPO" && c.IsOk);
        Assert.Contains(document.Checks, c => c.Text == "Wersje" && c.IsOk);
        Assert.False(document.HasVersionMismatch);
    }

    [Fact]
    public async Task Gpo_DelegatedEditor_AndPasswordSettings()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.WorkstationPasswords, GpoDocumentTab.Delegation);

        Assert.Equal(GpoDocumentTab.Delegation, document.SelectedTab);
        var lukasz = Assert.Single(document.Delegation, r => r.Name.EndsWith("lukasz.wisniewski", StringComparison.Ordinal));
        Assert.Equal(GpoPermissionLevel.Edit, lukasz.Level);
        Assert.Equal("Edycja ustawień", lukasz.LevelText);
        Assert.Equal("Tylko stacje robocze", document.SelectedWmiFilter?.Text);
        Assert.Contains(document.Settings, s => s.Name.EndsWith("MinimumPasswordLength", StringComparison.Ordinal) && s.Value == "12");
        Assert.Contains(document.Settings, s => s.Value.Contains("Łukaszem Wiśniewskim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gpo_VersionBehind_IsReportedOnTheStatusTab()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.DriveMappings, GpoDocumentTab.Status);

        Assert.True(document.HasVersionMismatch);
        var versions = Assert.Single(document.Checks, c => c.Text == "Wersje");
        Assert.False(versions.IsOk);
        Assert.Contains("replikacja SYSVOL", versions.Detail, StringComparison.Ordinal);
        Assert.True(document.AlignVersionsCommand.CanExecute(null));
        Assert.Contains(document.Settings, s => s.Value.Contains("Księgowość", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scope_OfComputersOu_ShowsLinksInOrder_AndInheritancePrecedence()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.ScopeAsync(h.Company.ComputersOu);

        Assert.Equal("Zasady grupy – Komputery", document.Title);
        Assert.Equal(["Stacje robocze – Zasady haseł", "Stacje robocze – Pulpit zdalny"], document.Links.Select(l => l.GpoName));
        Assert.Equal([1, 2], document.Links.Select(l => l.Order));
        Assert.False(document.Links[1].Enabled);
        Assert.False(document.BlocksInheritance);
        Assert.True(document.CanBlockInheritance);

        // Enforced domain link first, then the OU's own enabled link, then the domain's ordinary link (disabled link skipped).
        Assert.Equal(["Serwery – Zabezpieczenia", "Stacje robocze – Zasady haseł", "Default Domain Policy"], document.Inheritance.Select(i => i.GpoName));
        Assert.Equal([1, 2, 3], document.Inheritance.Select(i => i.Precedence));
        Assert.Equal("Tak", document.Inheritance[0].EnforcedText);
    }

    [Fact]
    public async Task Scope_OfBlockingOu_KeepsOnlyEnforcedLinksFromAbove()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.ScopeAsync(h.Company.EmployeesOu);

        Assert.True(document.BlocksInheritance);
        Assert.Equal("Dziedziczenie jest tu zablokowane.", document.BlockInheritanceText);
        Assert.Equal(["Serwery – Zabezpieczenia", "Użytkownicy – Mapowanie dysków"], document.Inheritance.Select(i => i.GpoName));
    }

    [Fact]
    public async Task Scope_OfSite_ShowsItsLinks_WithoutBlockInheritance()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.ScopeAsync(h.Sample.SiteDn);

        Assert.True(document.IsSite);
        Assert.False(document.CanBlockInheritance);
        Assert.False(document.ToggleBlockInheritanceCommand.CanExecute(null));
        Assert.Equal("Zasady grupy – Warszawa", document.Title);
        Assert.Equal(["Lokacja Warszawa – Drukarki"], document.Links.Select(l => l.GpoName));
        Assert.Equal(["Lokacja Warszawa – Drukarki"], document.Inheritance.Select(l => l.GpoName));
    }

    [Fact]
    public async Task WmiFilters_ListTheFilter_WithItsQueryAndGpos()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.WmiFiltersAsync();

        var row = Assert.Single(document.Rows);
        Assert.Equal("Tylko stacje robocze", row.Name);
        Assert.Equal("Systemy klienckie (ProductType = 1) – bez serwerów", row.Description);
        Assert.Contains("Win32_OperatingSystem WHERE ProductType = 1", row.QueriesText, StringComparison.Ordinal);
        Assert.Equal("Stacje robocze – Zasady haseł", row.GposText);
    }

    [Theory]
    [InlineData(SysvolUnavailableReason.AccessDenied, "odmowa dostępu")]
    [InlineData(SysvolUnavailableReason.LogonFailure, "odrzucił poświadczenia")]
    [InlineData(SysvolUnavailableReason.Unreachable, "nie można połączyć")]
    [InlineData(SysvolUnavailableReason.DistinguishedNameLogon, "nazwą wyróżniającą")]
    [InlineData(SysvolUnavailableReason.NotSupported, "nieobsługiwany")]
    [InlineData(SysvolUnavailableReason.Failed, "SYSVOL niedostępny")]
    public async Task SysvolUnavailable_ShowsTheReason_AndDisablesSysvolWrites_ButNotLinks(SysvolUnavailableReason reason, string text)
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.Sysvol.Unavailable = reason;
        using var objects = await h.ObjectsAsync();
        using var scope = await h.ScopeAsync(h.Company.ComputersOu);

        Assert.Contains(text, objects.SysvolWarning, StringComparison.Ordinal);
        Assert.Contains(h.B2.Session.Info.Server, objects.SysvolWarning, StringComparison.Ordinal);
        Assert.Equal(7, objects.Rows.Count);
        Assert.All(objects.Rows, r => Assert.Equal("–", r.SysvolVersionText));
        Assert.False(objects.NewCommand.CanExecute(null));
        Assert.False(objects.RestoreCommand.CanExecute(null));
        objects.Selection = [objects.Rows[0]];
        Assert.False(objects.CopyCommand.CanExecute(null));
        Assert.False(objects.BackupCommand.CanExecute(null));
        Assert.True(objects.RenameCommand.CanExecute(null));

        Assert.False(scope.CreateAndLinkCommand.CanExecute(null));
        Assert.True(scope.LinkExistingCommand.CanExecute(null));
        scope.SelectedLink = scope.Links[0];
        Assert.True(scope.ToggleEnforcedCommand.CanExecute(null));
    }

    [Fact]
    public async Task WithoutSysvolWriter_EveryDocumentSaysNotSupported()
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"), withWriter: false);
        using var document = await h.GpoAsync(h.Sample.WorkstationPasswords);

        Assert.Contains("not supported", document.SysvolWarning, StringComparison.Ordinal);
        Assert.False(document.AddSecurityFilterCommand.CanExecute(null));
        Assert.Contains(document.Checks, c => c.Text == "SYSVOL" && !c.IsOk);
        Assert.Empty(document.Settings);
        Assert.Equal(document.SysvolWarning, document.SettingsNote);
    }

    [Fact]
    public async Task PdcNote_NamesBothDomainControllers_WhenTheSessionIsNotOnThePdc()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var configuration = h.Session.Info.ConfigurationNamingContext;
        var servers = $"CN=Servers,CN=Warszawa,CN=Sites,{configuration}";
        h.Session.Seed(servers, new Dictionary<string, object?> { [Core.ActiveDirectory.Attr.ObjectClass] = new[] { "top", "serversContainer" } });
        var server = $"CN=DC02,{servers}";
        h.Session.Seed(server, new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "server" }, [Attr.DnsHostName] = "dc02.contoso.test" });
        h.Session.Seed($"CN=NTDS Settings,{server}", new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "nTDSDSA" } });
        h.Session.SeedAttributes(h.DomainDn, new Dictionary<string, object?> { ["fSMORoleOwner"] = $"CN=NTDS Settings,{server}" });

        using var document = await h.ObjectsAsync();

        Assert.True(document.HasPdcNote);
        Assert.Contains(h.Session.Info.Server, document.PdcNote, StringComparison.Ordinal);
        Assert.Contains("dc02.contoso.test", document.PdcNote, StringComparison.Ordinal);
        Assert.Contains("emulatora PDC", document.PdcNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Documents_ReloadAfterAnOperation_AndWhenTheLanguageChanges()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var objects = await h.ObjectsAsync();
        h.Windows.NewGpoName = "Laptopy – BitLocker";

        Assert.True(await h.Actions.NewAsync());
        await B2Harness.WaitUntilAsync(() => objects.Rows.Count == 8);
        Assert.Contains(objects.Rows, r => r.Name == "Laptopy – BitLocker");

        h.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        await B2Harness.WaitUntilAsync(() => objects.Title == "Group Policy Objects" && objects.Rows.Any(r => r.StatusText == "Enabled"));
    }

    [Fact]
    public async Task Disconnected_DocumentsShowNotConnected()
    {
        var h = await GroupPolicyHarness.CreateAsync();
        using var document = await h.ObjectsAsync();
        Assert.NotEmpty(document.Rows);

        await h.B2.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => document.Rows.Count == 0);
        Assert.NotNull(document.Error);
        Assert.False(document.NewCommand.CanExecute(null));
    }
}
