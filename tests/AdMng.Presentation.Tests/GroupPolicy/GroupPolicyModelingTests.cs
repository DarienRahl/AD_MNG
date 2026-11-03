using System.Globalization;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Modeling;
using AdMng.Presentation.Computers;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Modeling;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Shell;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// "Co dostanie komputer" (K5, design note docs/dev/k-computers-and-gpo.md 18) on the fake directory with the Polish GPO
/// management sample, its SYSVOL and the sample templates: the document for a computer, a user and a container, the
/// recalculation after the WMI, loopback and site choices, "Dlaczego", missing targets, SYSVOL unavailable, the CSV export
/// and the entry points.
/// </summary>
public sealed class GroupPolicyModelingTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static async Task<GroupPolicyHarness> HarnessAsync(bool withWriter = true)
    {
        var harness = await GroupPolicyHarness.CreateAsync(Polish, withWriter);
        harness.Session.ComputesTokenGroups = true;
        return harness;
    }

    private static Task<GroupPolicyModelingDocumentViewModel> ModelAsync(GroupPolicyHarness harness, string? computer = null, string? user = null, string? container = null) =>
        harness.OpenAsync<GroupPolicyModelingDocumentViewModel>(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Modeling)
        {
            ComputerDn = computer,
            UserDn = user,
            ContainerDn = container,
        });

    private static ModelGpoRowViewModel Gpo(ModelPartViewModel part, string name) => Assert.Single(part.Gpos, g => g.Name == name);

    [Fact]
    public async Task Computer_GetsTheGposGpmcWouldModel_WithOutcomesAndSettings()
    {
        var h = await HarnessAsync();

        var model = await ModelAsync(h, computer: h.Company.Computer1);

        Assert.Null(model.Error);
        Assert.Equal("Co dostanie komputer – WAW-PC001", model.Title);
        Assert.Equal("WAW-PC001 (contoso.test/Firma/Komputery)", model.ComputerText);
        Assert.Equal("(brak)", model.UserText);
        Assert.True(model.HasModel);
        Assert.False(model.IsStale);
        var part = model.ComputerPart;
        Assert.True(part.IsAvailable);
        Assert.Equal(["Serwery – Zabezpieczenia", "Stacje robocze – Zasady haseł", "Default Domain Policy"], part.Gpos.Where(g => g.Applies).Select(g => g.Name));
        Assert.Equal(["1", "2", "3"], part.Gpos.Where(g => g.Applies).Select(g => g.PrecedenceText));
        var servers = Gpo(part, "Serwery – Zabezpieczenia");
        Assert.Equal(("contoso.test", "Tak", "Zastosowany"), (servers.LocationText, servers.EnforcedText, servers.OutcomeText));
        Assert.Equal("Zastosowany, jeśli filtr WMI „Tylko stacje robocze” jest spełniony", Gpo(part, "Stacje robocze – Zasady haseł").OutcomeText);
        var remoteDesktop = Gpo(part, "Stacje robocze – Pulpit zdalny");
        Assert.Equal(("Łącze wyłączone", "–", "contoso.test/Firma/Komputery"), (remoteDesktop.OutcomeText, remoteDesktop.PrecedenceText, remoteDesktop.LocationText));
        Assert.DoesNotContain(part.Gpos, g => g.Name == "Lokacja Warszawa – Drukarki"); // no site: site links do not apply
        Assert.Contains("Lokacja nieznana: łącza lokacji nie zostały uwzględnione.", part.Warnings);

        var length = Assert.Single(part.Settings, s => s.Name == "Minimalna długość hasła");
        Assert.Equal(("12 znaków", "Stacje robocze – Zasady haseł", 1), (length.Value, length.WinnerGpo, length.OverriddenCount));
        Assert.Equal("Ustawienia systemu Windows › Ustawienia zabezpieczeń › Zasady konta › Zasady haseł", length.Area);
        Assert.Equal(h.Sample.WorkstationPasswords, length.GpoId);
        Assert.True(length.CanEdit);
        Assert.Equal(["CONTOSO\\Domain Computers", "CONTOSO\\WAW-PC001$", "Ta organizacja", "Użytkownicy uwierzytelnieni", "Wszyscy"], part.Groups);

        Assert.False(model.UserPart.IsAvailable);
        Assert.Equal("Model nie obejmuje użytkownika.", model.UserPart.UnavailableText);
        Assert.Equal("Pętla zwrotna: nie dotyczy (model bez komputera lub bez użytkownika).", model.LoopbackText);
        Assert.StartsWith("Komputer: 3 GPO, ", model.SummaryText, StringComparison.Ordinal);
        Assert.Equal(model.SummaryText, model.StatusText);
        Assert.Empty(model.Notes);
    }

    [Fact]
    public async Task Details_NameTheWinnerItsLinkAndTheOverriddenGpoWithItsValue()
    {
        var h = await HarnessAsync();
        var model = await ModelAsync(h, computer: h.Company.Computer1);
        var part = model.ComputerPart;
        Assert.Equal("Wybierz ustawienie, aby zobaczyć, dlaczego wygrywa.", part.DetailsText);

        part.SelectedSetting = part.Settings.Single(s => s.Name == "Minimalna długość hasła");

        var lines = part.DetailsText.Split(Environment.NewLine);
        Assert.Equal("Wygrywa: Stacje robocze – Zasady haseł (pierwszeństwo 2)", lines[0]);
        Assert.Equal("Łącze: contoso.test/Firma/Komputery, wymuszone: Nie, kolejność łącza: 1", lines[1]);
        Assert.Equal("Wartość: 12 znaków", lines[2]);
        Assert.Contains("Przesłonięte GPO:", lines);
        Assert.Equal("• Default Domain Policy (pierwszeństwo 3): 7 znaków", lines[^1]);

        part.SelectedSetting = part.Settings.Single(s => s.Name == "Debugowanie programów");
        Assert.Contains("wymuszone: Tak", part.DetailsText, StringComparison.Ordinal);
        Assert.EndsWith("Żaden inny zastosowany GPO nie konfiguruje tego ustawienia.", part.DetailsText, StringComparison.Ordinal);

        // A recalculation keeps the selected setting.
        await model.RecalculateCommand.ExecuteAsync(null);
        Assert.Equal("Debugowanie programów", model.ComputerPart.SelectedSetting?.Name);
    }

    [Fact]
    public async Task User_GetsTheDriveMappingsOnly_TheOthersAreBlockedOrDisabled()
    {
        var h = await HarnessAsync();

        var model = await ModelAsync(h, user: h.Company.JanKowalski);

        Assert.Equal("Co dostanie użytkownik – Jan Kowalski", model.Title);
        Assert.False(model.ComputerPart.IsAvailable);
        var part = model.UserPart;
        Assert.Equal(["Użytkownicy – Mapowanie dysków"], part.Gpos.Where(g => g.Applies).Select(g => g.Name));
        Assert.Equal("Konfiguracja użytkownika wyłączona", Gpo(part, "Serwery – Zabezpieczenia").OutcomeText);
        Assert.Equal("Zablokowany przez blokowanie dziedziczenia w contoso.test/Firma/Pracownicy", Gpo(part, "Default Domain Policy").OutcomeText);
        // The drive map is one row (its item's settings together), in GPMC's preference folder.
        var drive = Assert.Single(part.Settings, s => s.Area == "Preferencje › Ustawienia systemu Windows › Mapowania dysków");
        Assert.Equal(("K:", "Użytkownicy – Mapowanie dysków"), (drive.Name, drive.WinnerGpo));
        // The narrow area column shows the end of the path (its last two segments), the tooltip the whole path.
        Assert.Equal("… › Ustawienia systemu Windows › Mapowania dysków", drive.ShortArea);
        Assert.Contains("Księgowość", drive.Value, StringComparison.Ordinal);
        Assert.Contains("Bez komputera: nie sprawdzono prawa odczytu komputera (MS16-072) ani pętli zwrotnej.", part.Warnings);
        Assert.StartsWith("Użytkownik: 1 GPO, ", model.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Container_ModelsANewComputerAndANewUserThere()
    {
        var h = await HarnessAsync();

        var model = await ModelAsync(h, container: h.Company.ComputersOu);

        Assert.Equal("Nowy komputer w contoso.test/Firma/Komputery", model.ComputerText);
        Assert.Equal("Nowy użytkownik w contoso.test/Firma/Komputery", model.UserText);
        Assert.Equal(["Serwery – Zabezpieczenia", "Stacje robocze – Zasady haseł", "Default Domain Policy"], model.ComputerPart.Gpos.Where(g => g.Applies).Select(g => g.Name));
        Assert.True(model.UserPart.IsAvailable);
        Assert.Contains("Ta organizacja", model.ComputerPart.Groups);
        Assert.Equal("Pętla zwrotna: nieskonfigurowana w GPO komputera.", model.LoopbackText);
        Assert.Contains("; Użytkownik: 0 GPO, ", model.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recalculate_FollowsTheWmiAssumptionTheSiteAndLoopback()
    {
        var h = await HarnessAsync();
        var model = await ModelAsync(h, computer: h.Company.Computer1, user: h.Company.JanKowalski);
        Assert.Equal(["(nieznana)", "Warszawa"], model.Sites.Select(s => s.Name));

        model.WmiFilters = WmiFilterAssumption.AssumeFalse;
        Assert.True(model.IsStale);
        await model.RecalculateCommand.ExecuteAsync(null);
        Assert.False(model.IsStale);
        Assert.Equal("Filtr WMI „Tylko stacje robocze” przyjęty jako niespełniony", Gpo(model.ComputerPart, "Stacje robocze – Zasady haseł").OutcomeText);
        Assert.Equal(["Serwery – Zabezpieczenia", "Default Domain Policy"], model.ComputerPart.Gpos.Where(g => g.Applies).Select(g => g.Name));

        model.SelectedSite = model.Sites.Single(s => s.Name == "Warszawa");
        await model.RecalculateCommand.ExecuteAsync(null);
        Assert.Equal("Warszawa", model.SelectedSite?.Name); // the operator's site is kept
        Assert.Equal("Pusty (wersja 0)", Gpo(model.ComputerPart, "Lokacja Warszawa – Drukarki").OutcomeText);
        Assert.DoesNotContain("Lokacja nieznana: łącza lokacji nie zostały uwzględnione.", model.ComputerPart.Warnings);

        model.Loopback = LoopbackChoice.Replace;
        await model.RecalculateCommand.ExecuteAsync(null);
        Assert.Equal("Pętla zwrotna: zastępowanie – wybrana dla tego modelu.", model.LoopbackText);
        Assert.DoesNotContain(model.UserPart.Gpos, g => g.Name == "Użytkownicy – Mapowanie dysków");
        Assert.All(model.UserPart.Gpos, g => Assert.True(g.Gpo.FromLoopback));
        Assert.Contains("(pętla zwrotna: łącze komputera)", Gpo(model.UserPart, "Serwery – Zabezpieczenia").OutcomeText, StringComparison.Ordinal);

        model.Loopback = LoopbackChoice.Merge;
        await model.RecalculateCommand.ExecuteAsync(null);
        // Merge: the user's GPOs, then the computer's chain (the site's printers – blocked for the user's own chain).
        Assert.Equal(["Lokacja Warszawa – Drukarki", "Użytkownicy – Mapowanie dysków"], model.UserPart.Gpos.Where(g => g.Applies).Select(g => g.Name));
        Assert.Contains(model.UserPart.Gpos, g => g is { Applies: true, Name: "Lokacja Warszawa – Drukarki", Gpo.FromLoopback: true });
        Assert.Contains(model.UserPart.Gpos, g => g is { Applies: false, Name: "Lokacja Warszawa – Drukarki", Gpo.FromLoopback: false });
    }

    [Fact]
    public async Task AdministrativeTemplates_ArePoliciesWithTheirOptions_AndTheHigherPrecedenceWins()
    {
        var h = await HarnessAsync();
        h.AddCentralStore();
        var templates = SampleAdministrativeTemplates.LoadPolish();
        var hidePreview = templates.Find(SampleAdministrativeTemplates.HidePreview)!;
        var updates = templates.Find(SampleAdministrativeTemplates.Updates)!;
        var domain = new RegistryPolicyDocument();
        PolicyEngine.Write(hidePreview.Policy, PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>()), domain);
        h.Sysvol.Put(h.Sample.DefaultDomainPolicy, "Machine/Registry.pol", domain.ToBytes());
        var workstations = new RegistryPolicyDocument();
        PolicyEngine.Write(hidePreview.Policy, PolicySetting.Disabled, workstations);
        PolicyEngine.Write(updates.Policy, PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>
        {
            ["Tryb"] = new EnumElementValue(1),
            ["Dzien"] = new NumberElementValue(3),
        }), workstations);
        h.Sysvol.Put(h.Sample.WorkstationPasswords, "Machine/Registry.pol", workstations.ToBytes());

        var model = await ModelAsync(h, computer: h.Company.Computer1);

        var part = model.ComputerPart;
        var preview = Assert.Single(part.Settings, s => s.Name == hidePreview.DisplayName);
        Assert.Equal(("Wyłączone", "Stacje robocze – Zasady haseł", 1), (preview.Value, preview.WinnerGpo, preview.OverriddenCount));
        Assert.StartsWith("Szablony administracyjne › ", preview.Area, StringComparison.Ordinal);
        part.SelectedSetting = preview;
        Assert.EndsWith("• Default Domain Policy (pierwszeństwo 3): Włączone", part.DetailsText, StringComparison.Ordinal);

        var update = Assert.Single(part.Settings, s => s.Name == updates.DisplayName);
        Assert.StartsWith("Włączone; ", update.Value, StringComparison.Ordinal);
        Assert.Contains(": 3", update.Value, StringComparison.Ordinal);
        Assert.Contains(updates.Text(((EnumElement)updates.Policy.Elements.Single(e => e.Id == "Tryb")).Items[1].DisplayName), update.Value, StringComparison.Ordinal);
        Assert.Equal("Szablony administracyjne › " + string.Join(" › ", updates.Category!.PathNames()), update.Area);
        Assert.DoesNotContain(part.Settings, s => s.Name.StartsWith(@"HKLM\Software\Policies\Contoso", StringComparison.Ordinal)); // described values are policies
        Assert.Equal(GroupPolicySettingFocus.ForPolicy(PolicyClass.Machine, updates.Id), update.Focus);
    }

    [Fact]
    public async Task MissingTargets_AreNotes_NotErrors()
    {
        var h = await HarnessAsync();
        var missing = "CN=Nie ma,OU=Komputery,OU=Firma," + h.DomainDn;

        var model = await ModelAsync(h, computer: missing);

        Assert.Null(model.Error);
        Assert.False(model.HasModel);
        Assert.Equal(["Konto contoso.test/Firma/Komputery/Nie ma nie istnieje."], model.Notes);
        Assert.False(model.ExportCsvCommand.CanExecute(null));

        // A missing container next to an existing user: the user part is still modelled.
        var other = await ModelAsync(h, user: h.Company.JanKowalski);
        h.B2.Dialogs.PickedContainers.Enqueue("OU=Usunięta,OU=Firma," + h.DomainDn);
        await other.PickComputerContainerCommand.ExecuteAsync(null);
        await other.RecalculateCommand.ExecuteAsync(null);
        Assert.True(other.HasModel);
        Assert.Equal(["Kontener contoso.test/Firma/Usunięta nie istnieje."], other.Notes);
        Assert.False(other.ComputerPart.IsAvailable);
        Assert.True(other.UserPart.IsAvailable);

        // No target at all.
        other.ClearComputerCommand.Execute(null);
        other.ClearUserCommand.Execute(null);
        await other.RecalculateCommand.ExecuteAsync(null);
        Assert.Equal(["Wybierz komputer lub użytkownika."], other.Notes);
        Assert.Equal("Modelowanie zasad grupy", other.Title);
    }

    [Fact]
    public async Task SysvolUnavailable_ListsTheGpos_WithoutSettings_AndSaysWhy()
    {
        var h = await HarnessAsync(withWriter: false);

        var model = await ModelAsync(h, computer: h.Company.Computer1);

        Assert.Null(model.Error);
        Assert.True(model.HasSysvolWarning);
        Assert.Contains("SYSVOL niedostępny: ustawienia GPO nie zostały odczytane; listy GPO są pełne.", model.Notes);
        Assert.Equal(3, model.ComputerPart.Gpos.Count(g => g.Applies));
        Assert.Empty(model.ComputerPart.Settings);
        Assert.DoesNotContain(model.ComputerPart.Warnings, w => w.Contains("SYSVOL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PickComputer_ChangesTheInput_UntilRecalculated()
    {
        var h = await HarnessAsync();
        var model = await ModelAsync(h, user: h.Company.JanKowalski);
        h.B2.Dialogs.PickedObjects.Enqueue([h.Company.Computer2]);

        await model.PickComputerCommand.ExecuteAsync(null);

        Assert.Equal(ModelingTargetChoice.Account(h.Company.Computer2), model.ComputerChoice);
        Assert.True(model.IsStale);
        Assert.Equal("Co dostanie komputer – WAW-PC002", model.Title);
        Assert.False(model.ComputerPart.IsAvailable);
        await model.RecalculateCommand.ExecuteAsync(null);
        Assert.True(model.ComputerPart.IsAvailable);
        Assert.Equal("Pętla zwrotna: nieskonfigurowana w GPO komputera.", model.LoopbackText);
    }

    [Fact]
    public async Task Site_OfTheComputer_IsTakenFromItsSubnet_UntilTheOperatorPicksOne()
    {
        var h = await HarnessAsync();
        var sites = Core.Ldap.DistinguishedName.Parse(h.Sample.SiteDn).Parent!.ToString();
        var subnets = "CN=Subnets," + sites;
        if (!h.Session.Exists(subnets))
        {
            h.Session.Seed(subnets, new Dictionary<string, object?> { [Core.ActiveDirectory.Attr.ObjectClass] = new[] { "top", "subnetContainer" } });
        }

        h.Session.Seed("CN=10.10.0.0/16," + subnets, new Dictionary<string, object?>
        {
            [Core.ActiveDirectory.Attr.ObjectClass] = new[] { "top", "subnet" },
            [Core.ActiveDirectory.Attr.Cn] = "10.10.0.0/16",
            ["siteObject"] = h.Sample.SiteDn,
        });
        var resolver = new FakeHostAddressResolver().Add("WAW-PC001", "10.10.4.2").Add("WAW-PC001.contoso.test", "10.10.4.2");
        var model = new GroupPolicyModelingDocumentViewModel(h.Services, h.Actions, h.Definitions,
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Modeling) { ComputerDn = h.Company.Computer1 }, resolver);

        await model.LoadAsync();

        Assert.Equal("Warszawa", model.SelectedSite?.Name);
        Assert.False(model.IsStale);
        Assert.Equal("Pusty (wersja 0)", Gpo(model.ComputerPart, "Lokacja Warszawa – Drukarki").OutcomeText);

        // The operator's "(nieznana)" is kept on the next calculation.
        model.SelectedSite = model.Sites[0];
        await model.RecalculateCommand.ExecuteAsync(null);
        Assert.Null(model.SelectedSite?.Dn);
        Assert.DoesNotContain(model.ComputerPart.Gpos, g => g.Name == "Lokacja Warszawa – Drukarki");
    }

    [Fact]
    public async Task FailedRecalculation_LeavesNoModelOfTheEarlierInputs_AndTheExportWaitsForAModel()
    {
        var h = await HarnessAsync();
        var model = await ModelAsync(h, computer: h.Company.Computer1);
        Assert.True(model.ExportCsvCommand.CanExecute(null));

        h.B2.Dialogs.PickedObjects.Enqueue([h.Company.Computer2]);
        await model.PickComputerCommand.ExecuteAsync(null);
        Assert.False(model.ExportCsvCommand.CanExecute(null)); // the shown model is WAW-PC001's
        h.Session.FailNext(FakeOperationKind.Search, null, Core.Directory.DirectoryErrorKind.ServerUnavailable, times: 10);
        await model.RecalculateCommand.ExecuteAsync(null);

        Assert.NotNull(model.Error);
        Assert.False(model.HasModel);
        Assert.False(model.ComputerPart.IsAvailable);
        Assert.False(model.ExportCsvCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExportCsv_WritesTheGpoListsAndTheSettingsOfBothParts_ToTheChosenFile()
    {
        var h = await HarnessAsync();
        var model = await ModelAsync(h, computer: h.Company.Computer1, user: h.Company.JanKowalski);
        var path = Path.Combine(Path.GetTempPath(), "admng-k5-" + Guid.NewGuid().ToString("N") + ".csv");
        h.B2.Files.NextSavePath = path;
        try
        {
            await model.ExportCsvCommand.ExecuteAsync(null);

            Assert.StartsWith("Co dostanie komputer – WAW-PC001_", h.B2.Files.LastDefaultFileName, StringComparison.Ordinal);
            var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
            var separator = lines[0][lines[0].IndexOf("Sekcja", StringComparison.Ordinal) - 1];
            Assert.Equal(string.Join(separator, "Część", "Sekcja", "Pierwszeństwo", "Obiekt zasad grupy", "Lokalizacja", "Wymuszone", "Kolejność łącza", "Wynik",
                "Ustawienie", "Wartość", "Przesłonięte", "Uwagi"), lines[0]);
            Assert.Contains(string.Join(separator, "Komputer", "GPO", "1", "Serwery – Zabezpieczenia", "contoso.test", "Tak", model.ComputerPart.Gpos[0].LinkOrderText, "Zastosowany", "", "", "", ""), lines);
            Assert.Contains(lines, l => l.StartsWith(string.Join(separator, "Komputer", "Ustawienie", "", "Stacje robocze – Zasady haseł"), StringComparison.Ordinal)
                && l.Contains("Minimalna długość hasła" + separator + "12 znaków" + separator + "1", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith(string.Join(separator, "Użytkownik", "GPO", "1", "Użytkownicy – Mapowanie dysków"), StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith(string.Join(separator, "Użytkownik", "Ustawienie"), StringComparison.Ordinal));
            Assert.Contains(h.B2.Notifications.Items, n => n.Message.Contains(Path.GetFileName(path), StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EntryPoints_ComputerAndUserCommands_FollowTheSelection_AndReuseTheTab()
    {
        var h = await HarnessAsync();
        using var shell = h.CreateShell();
        await shell.InitializeAsync();
        Assert.Contains(shell.CommandDescriptors, d => d.Id == "gpo.model");
        Assert.Contains(shell.CommandDescriptors, d => d.Id == "gpo.modelUser");

        await shell.NavigateAsync(ListLocation.ForContainer(h.Company.ComputersOu, "Komputery", Icons.ObjectIcon.OrganizationalUnit));
        Assert.False(shell.ModelComputerGroupPolicyCommand.CanExecute(null));
        Select(shell, "WAW-PC001");
        Assert.True(shell.ModelComputerGroupPolicyCommand.CanExecute(null));
        Assert.False(shell.ModelUserGroupPolicyCommand.CanExecute(null));
        Select(shell, "WAW-PC001", "WAW-PC002");
        Assert.False(shell.ModelComputerGroupPolicyCommand.CanExecute(null));

        Select(shell, "WAW-PC001");
        shell.ModelComputerGroupPolicyCommand.Execute(null);
        var model = Assert.IsType<GroupPolicyModelingDocumentViewModel>(shell.SelectedTab);
        Assert.Equal(ModelingTargetChoice.Account(h.Company.Computer1), model.ComputerChoice);
        shell.ModelComputerGroupPolicyCommand.Execute(h.Company.Computer1);
        Assert.Same(model, shell.SelectedTab);
        Assert.Single(shell.GroupPolicyDocuments.OfType<GroupPolicyModelingDocumentViewModel>());

        await shell.NavigateAsync(ListLocation.ForContainer(h.Company.ItOu, "IT", Icons.ObjectIcon.OrganizationalUnit));
        Select(shell, "Jan Kowalski");
        Assert.False(shell.ModelComputerGroupPolicyCommand.CanExecute(null));
        Assert.True(shell.ModelUserGroupPolicyCommand.CanExecute(null));
        shell.ModelUserGroupPolicyCommand.Execute(null);
        var user = Assert.IsType<GroupPolicyModelingDocumentViewModel>(shell.SelectedTab);
        Assert.NotSame(model, user);
        Assert.Equal(ModelingTargetChoice.Account(h.Company.JanKowalski), user.UserChoice);

        // An inetOrgPerson is a user account too.
        var anna = "CN=Anna Zielińska," + h.Company.ItOu;
        h.Session.Seed(anna, new Dictionary<string, object?>
        {
            [Core.ActiveDirectory.Attr.ObjectClass] = new[] { "top", "person", "organizationalPerson", "user", "inetOrgPerson" },
            [Core.ActiveDirectory.Attr.Cn] = "Anna Zielińska",
            [Core.ActiveDirectory.Attr.SamAccountName] = "anna.zielinska",
        });
        await shell.NavigateAsync(ListLocation.ForContainer(h.Company.ItOu, "IT", Icons.ObjectIcon.OrganizationalUnit));
        Select(shell, "Anna Zielińska");
        Assert.True(shell.ModelUserGroupPolicyCommand.CanExecute(null));
    }

    [Fact]
    public async Task EntryPoints_ScopeTabAndComputerStatusPage_OpenTheModelThroughTheShell()
    {
        var h = await HarnessAsync();
        using var shell = h.CreateShell();
        await shell.InitializeAsync();

        var scope = await h.ScopeAsync(h.Company.ComputersOu);
        Assert.True(scope.ModelPoliciesCommand.CanExecute(null));
        scope.ModelPoliciesCommand.Execute(null);
        var model = Assert.IsType<GroupPolicyModelingDocumentViewModel>(shell.SelectedTab);
        Assert.Equal(ModelingTargetChoice.Container(h.Company.ComputersOu), model.ComputerChoice);
        Assert.Equal(ModelingTargetChoice.Container(h.Company.ComputersOu), model.UserChoice);
        Assert.Equal("Co dostanie komputer – Komputery", model.Title);

        var site = await h.ScopeAsync(h.Sample.SiteDn);
        Assert.False(site.ModelPoliciesCommand.CanExecute(null));

        // The computer property sheet's "Stan" page goes through the window's computer actions.
        var actions = new ComputerActions(h.Ui, h.B2.Planner, groupPolicy: h.Services);
        Assert.True(actions.CanModelGroupPolicy);
        Assert.True(actions.ModelGroupPolicy(h.Company.Computer2));
        var computer = Assert.IsType<GroupPolicyModelingDocumentViewModel>(shell.SelectedTab);
        Assert.Equal(ModelingTargetChoice.Account(h.Company.Computer2), computer.ComputerChoice);
        Assert.False(new ComputerActions(h.Ui, h.B2.Planner).CanModelGroupPolicy);
    }

    [Fact]
    public async Task Factory_MatchesOneTabPerTargetSet()
    {
        var h = await HarnessAsync();
        var model = await ModelAsync(h, computer: h.Company.Computer1);

        Assert.True(GroupPolicyViewModelFactory.Matches(model, new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Modeling) { ComputerDn = h.Company.Computer1.ToUpperInvariant() }));
        Assert.False(GroupPolicyViewModelFactory.Matches(model, new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Modeling) { ComputerDn = h.Company.Computer1, UserDn = h.Company.JanKowalski }));
        Assert.False(GroupPolicyViewModelFactory.Matches(model, new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Modeling) { ContainerDn = h.Company.ComputersOu }));
        Assert.False(GroupPolicyViewModelFactory.Matches(model, new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Scope) { ContainerDn = h.Company.ComputersOu }));
        model.Dispose();
    }

    private static void Select(ShellViewModel shell, params string[] names)
    {
        var document = shell.SelectedDocument!;
        var rows = names.Select(n => document.Items.Single(r => r.Name == n)).ToList();
        document.SelectedItem = rows[0];
        document.SelectedItems = rows;
    }
}
