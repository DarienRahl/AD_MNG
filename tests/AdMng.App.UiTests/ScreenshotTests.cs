using System.Globalization;
using System.IO;
using System.Windows;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.EventLogs;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.App.Views.Watchdog;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.Ldap;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.Modeling;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Watchdog;
using AdMng.Testing;
using AdMng.Testing.EventLogs;
using AdMng.Watchdog.Testing.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// Screenshots of the program for every release (CI step "Screenshots", published to the "screenshots" branch): the
/// Polish UI over the sample company, one test per picture, each with its own host. They run only when
/// <see cref="ScreenshotWriter.DirectoryVariable"/> names the output folder and skip themselves otherwise, so the
/// normal UI test run is unaffected.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ScreenshotTests
{
    private const string Language = "pl";
    private const double MainWindowWidth = 1440;
    private const double MainWindowHeight = 900;

    /// <summary>More people in the IT unit than the two of the sample company (one of them disabled).</summary>
    private static readonly ItPerson[] ItStaff =
    [
        new("Agnieszka", "Zielińska", "Administratorka sieci", "Gdańsk", "agnieszka.zielinska"),
        new("Michał", "Wróbel", "Specjalista ds. bezpieczeństwa", "Warszawa", "michal.wrobel"),
        new("Katarzyna", "Mazur", "Analityczka danych", "Wrocław", "katarzyna.mazur"),
        new("Tomasz", "Jabłoński", "Stażysta – wsparcie techniczne", "Łódź", "tomasz.jablonski", Disabled: true),
    ];

    private static readonly ScreenshotInfo MainWindowLight = new(
        "01-main-window.png",
        "Okno główne – jednostka organizacyjna IT, szczegóły zaznaczonego użytkownika",
        "Main window – the IT organizational unit with the details of the selected user");

    private static readonly ScreenshotInfo MainWindowDark = new(
        "02-main-window-dark.png",
        "Okno główne w motywie ciemnym",
        "Main window in the dark theme");

    private static readonly ScreenshotInfo PropertiesGeneral = new(
        "03-user-properties-general.png",
        "Właściwości użytkownika – karta Ogólne",
        "User properties – General tab");

    private static readonly ScreenshotInfo PropertiesAccount = new(
        "04-user-properties-account.png",
        "Właściwości użytkownika – karta Konto",
        "User properties – Account tab");

    private static readonly ScreenshotInfo LogonHoursDialog = new(
        "14-logon-hours.png",
        "Godziny logowania – tygodniowy harmonogram konta w czasie lokalnym, zaznaczone godziny pracy",
        "Logon hours – the weekly schedule of an account in local time, office hours selected");

    private static readonly ScreenshotInfo BatchPreview = new(
        "05-batch-editor-preview.png",
        "Edytor zbiorczy – podgląd zmian (przed → po) przed wykonaniem",
        "Batch editor – preview of the changes (before → after) before running them");

    private static readonly ScreenshotInfo RecycleBin = new(
        "06-recycle-bin.png",
        "Kosz Active Directory – usunięte obiekty do przywrócenia",
        "Active Directory Recycle Bin – deleted objects to restore");

    private static readonly ScreenshotInfo Find = new(
        "07-find.png",
        "Wyszukiwanie – użytkownicy z Warszawy (warunek zaawansowany)",
        "Find – users in Warsaw (advanced condition)");

    private static readonly ScreenshotInfo Connect = new(
        "08-connect.png",
        "Połączenie z domeną",
        "Connecting to a domain");

    private static readonly ScreenshotInfo WatchdogEvents = new(
        "09-watchdog-events.png",
        "Watchdog – zdarzenia (filtr, lista i szczegóły blokady konta)",
        "Watchdog – events (filter, feed and the details of an account lockout)");

    private static readonly ScreenshotInfo WatchdogEventDetails = new(
        "10-watchdog-event-details.png",
        "Watchdog – szczegóły zdarzenia: co się zmieniło, kto i skąd",
        "Watchdog – event details: what changed, who and from where");

    private static readonly ScreenshotInfo WatchdogRules = new(
        "11-watchdog-rules.png",
        "Watchdog – reguły (lista i edytor reguły)",
        "Watchdog – rules (list and rule editor)");

    private static readonly ScreenshotInfo WatchdogChannels = new(
        "12-watchdog-channels.png",
        "Watchdog – kanały i SMTP",
        "Watchdog – channels and SMTP");

    private static readonly ScreenshotInfo WatchdogStatus = new(
        "13-watchdog-status.png",
        "Watchdog – stan usługi",
        "Watchdog – service status");

    private static readonly ScreenshotInfo WatchdogCertificate = new(
        "15-watchdog-certificate.png",
        "Watchdog – potwierdzenie certyfikatu usługi przy pierwszym połączeniu (odcisk SHA-256 do porównania z serwerem)",
        "Watchdog – confirming the service certificate on the first connection (SHA-256 fingerprint to compare with the server)");

    private static readonly ScreenshotInfo WatchdogSources = new(
        "16-watchdog-sources.png",
        "Watchdog – źródła i retencja (OpenWEC, treść GPO, rekordy logowań)",
        "Watchdog – sources and retention (OpenWEC, GPO content, logon records)");

    private static readonly ScreenshotInfo AccountReview = new(
        "17-account-review.png",
        "Przegląd kont – kafelki sprawdzeń i lista kont, którym wkrótce wygaśnie hasło",
        "Account review – the check tiles and the accounts whose password expires soon");

    private static readonly ScreenshotInfo PrivilegedAccess = new(
        "19-privileged-access.png",
        "Przegląd kont – zakładka Uprawnienia: konta z uprawnieniami ponad zwykłego użytkownika, poziomy i ścieżka uprawnienia",
        "Account review – the Privileges tab: accounts with rights beyond a plain user, their levels and the path of a right");

    private static readonly ScreenshotInfo UserMessages = new(
        "18-user-messages.png",
        "Wiadomości do użytkowników – podgląd wiadomości „hasło wygasa” dla każdego odbiorcy przed wysyłką",
        "Messages to users – the preview of the \"password expires\" message of every recipient before sending");

    private static readonly ScreenshotInfo TwoConnections = new(
        "20-two-connections.png",
        "Dwa połączenia jednocześnie – contoso.test i fabrikam.test w osobnych oknach, każde z własnym kolorem i kontem",
        "Two connections at once – contoso.test and fabrikam.test in separate windows, each with its own colour and account");

    private static readonly ScreenshotInfo ComputerReview = new(
        "22-computers.png",
        "Przegląd komputerów – systemy po końcu wsparcia, kafelki wersji Windows i porządku, szczegóły zaznaczonego komputera",
        "Computer review – systems past the end of support, the release and housekeeping tiles, the details of the selected computer");

    private static readonly ScreenshotInfo CompareObjects = new(
        "21-compare-objects.png",
        "Porównanie obiektów między połączeniami – Łukasz Wiśniewski w contoso.test i fabrikam.test, tylko różnice (stanowisko, miasto)",
        "Comparing objects between connections – Łukasz Wiśniewski in contoso.test and fabrikam.test, differences only (title, city)");

    private static readonly ScreenshotInfo GroupPolicy = new(
        "23-group-policy.png",
        "Zasady grupy jednostki Komputery – połączone GPO (jedno wyłączone), dziedziczenie z wymuszonym GPO domeny, obiekty zasad grupy w drzewie",
        "Group Policy of the Komputery unit – linked GPOs (one disabled), inheritance with the domain's enforced GPO, the Group Policy Objects in the tree");

    private static readonly ScreenshotInfo AdministrativeTemplates = new(
        "29-administrative-templates.png",
        "Szablony administracyjne GPO – kategorie z magazynu centralnego, włączane ustawienie z opcjami i pomocą, niezapisana zmiana",
        "Administrative templates of a GPO – the categories of the central store, a setting being enabled with its options and help, an unsaved change");

    private static readonly ScreenshotInfo CentralStoreUpdate = new(
        "30-central-store-update.png",
        "Aktualizacja szablonów ADMX – paczka wskazana przez operatora, języki, pliki magazynu centralnego (nowe, zastąpiony, bez zmian) i kopia zapasowa przed zapisem",
        "Updating the ADMX templates – a package the operator chose, its languages, the central store's files (new, replaced, unchanged) and the backup before writing");

    private static readonly ScreenshotInfo GroupPolicyModel = new(
        "31-group-policy-model.png",
        "Co dostanie komputer – GPO komputera z wynikiem (wymuszony, filtr WMI, łącze wyłączone), zwycięskie ustawienia i „Dlaczego” z przesłoniętym GPO",
        "What the computer gets – the computer's GPOs with their results (enforced, WMI filter, disabled link), the winning settings and “Why” with the overridden GPO");

    private static readonly ScreenshotInfo LockoutSource = new(
        "32-lockout-source.png",
        "Skąd blokada konta – Jan Kowalski: stan na kontrolerach domeny, próg z zasad domeny, źródła złych haseł (stacja WAW-PC001 i jej adres) z wyjaśnionymi kodami i sprawdzony komputer źródłowy z zadaniem zaplanowanym na zapisanym haśle",
        "Where the lockout came from – Jan Kowalski: the state on the domain controllers, the domain policy's threshold, the sources of the bad passwords (the workstation WAW-PC001 and its address) with the codes explained, and the source computer checked, with a scheduled task on a saved password");

    [Fact]
    public Task Screenshot32_EventLogs()
    {
        var connector = EventLogUiSupport.CreateConnector();
        return ShootAsync(ThemePreference.Light, connect: false, configure: EventLogUiSupport.Use(connector), shoot: async context =>
        {
            // Track E (design note docs/dev/e-event-logs.md 4.1): Jan Kowalski locked out by an old password kept on WAW-PC001
            // (a mapped drive and a scheduled task), the investigation over both DCs' Security logs, the workstation checked.
            await EventLogUiSupport.AddDomainControllersAsync(context);
            EventLogUiSupport.LockOutJan(context, connector);
            var remote = Assert.IsType<FakeRemoteComputerClient>(context.Host.Services.GetRequiredService<Core.Computers.Remote.IRemoteComputerClient>());
            var computer = new FakeRemoteComputer();
            computer.Users.Add(new Core.Computers.Remote.LoggedOnUser("CONTOSO\\jan.kowalski", 2, DateTimeOffset.UtcNow.AddDays(-3), IsConsole: true));
            computer.ScheduledTasks.Add(new Core.Computers.Remote.RemoteScheduledTask(
                "\\Firma\\", "Synchronizacja dysku Z", "jan.kowalski@contoso.test",
                Core.Computers.Remote.RemoteTaskLogonType.Password, Core.Computers.Remote.RemoteTaskState.Ready, true));
            remote.Computers["waw-pc001.contoso.test"] = computer;

            await context.ConnectAsync();
            var window = context.Services.GetRequiredService<MainWindow>();
            await ScreenshotWriter.ShowWithClientSizeAsync(window, MainWindowWidth, MainWindowHeight);
            var shell = window.Shell;
            await shell.InitializeAsync();
            var document = Assert.IsType<Presentation.EventLogs.LockoutDocumentViewModel>(shell.OpenEventLogDocument(
                new Presentation.EventLogs.EventLogDocumentRequest(Presentation.EventLogs.EventLogDocumentKind.Lockout)
                {
                    UserDn = context.Company.JanKowalski,
                    Name = "Jan Kowalski",
                }));
            await UiWait.UntilAsync(() => !document.IsLoading && document.HasReport, "the lockout report of Jan Kowalski");
            document.SelectedSource = document.Sources.Single(s => s.Value == EventLogSamples.WawPc001);
            await document.PivotCommand.ExecuteAsync(null);
            await UiTestSupport.WaitForIdleAsync(rounds: 3);

            Assert.Null(document.Error);
            Assert.True(document.IsLockedNow);
            Assert.NotNull(document.PivotLines);
            Assert.Contains(document.PivotLines, l => l.Contains("Synchronizacja dysku Z", StringComparison.Ordinal));
            await ScreenshotWriter.SaveAsync(window, LockoutSource, dark: false);
        });
    }

    private static readonly ScreenshotInfo Readiness = new(
        "33-readiness.png",
        "Gotowość środowiska – pełne sprawdzenie z komputerem testowym: połączenie, katalog, SYSVOL i zasady grupy, zdalne akcje, dzienniki zdarzeń każdego kontrolera, Watchdog, Outlook i ten komputer; każdy punkt z objaśnieniem, podpowiedzią i rozdziałem instrukcji",
        "Environment readiness – the full check with a test computer: connection, directory, SYSVOL and Group Policy, remote actions, every domain controller's event logs, Watchdog, Outlook and this computer; every check with its explanation, remedy and documentation chapter");

    [Fact]
    public Task Screenshot33_Readiness()
    {
        var connector = EventLogUiSupport.CreateConnector();
        return ShootAsync(ThemePreference.Light, connect: false, configure: EventLogUiSupport.Use(connector), shoot: async context =>
        {
            // Stage S (design note docs/dev/s-pilot-readiness.md 2): the full check over the in-memory directory with two DCs
            // and the test computer WAW-PC001, read only.
            await EventLogUiSupport.AddDomainControllersAsync(context);
            await context.ConnectAsync();
            var window = context.Services.GetRequiredService<MainWindow>();
            await ScreenshotWriter.ShowWithClientSizeAsync(window, MainWindowWidth, MainWindowHeight);
            var shell = window.Shell;
            await shell.InitializeAsync();
            shell.ShowReadinessCommand.Execute(null);
            var document = Assert.IsType<Presentation.Readiness.ReadinessDocumentViewModel>(shell.SelectedTab);
            await UiWait.UntilAsync(() => !document.IsLoading && document.HasResult, "the quick check");
            document.TestComputerText = "WAW-PC001";
            await document.FullCheckCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => !document.IsLoading, "the full check");
            await UiTestSupport.WaitForIdleAsync(rounds: 3);

            Assert.Equal(Presentation.Readiness.ReadinessDepth.Full, document.LastDepth);
            Assert.Empty(context.Sysvol.Writes);
            await ScreenshotWriter.SaveAsync(window, Readiness, dark: false);
        });
    }

    [Fact]
    public Task Screenshot29_AdministrativeTemplates() => ShootAsync(ThemePreference.Light, connect: false, shoot: async context =>
    {
        // K3 (design note docs/dev/k-computers-and-gpo.md 6): the editor of a GPO over the sample templates in the central store.
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        foreach (var (path, content) in SampleAdministrativeTemplates.Files())
        {
            context.Sysvol.PutPolicyDefinition(path, content);
        }

        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(window, MainWindowWidth, MainWindowHeight);
        var shell = window.Shell;
        await shell.InitializeAsync();
        var editor = Assert.IsType<AdministrativeTemplatesEditorViewModel>(shell.OpenGroupPolicyDocument(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.AdministrativeTemplates) { GpoId = sample.WorkstationPasswords, Name = "Stacje robocze – Zasady haseł" }));
        await UiWait.UntilAsync(() => !editor.IsLoading && editor.Roots.Count == 2, "the administrative templates");
        var windowsComponents = editor.Roots[0].Children.Single(c => c.Name == "Składniki systemu Windows");
        windowsComponents.IsExpanded = true;
        var updates = windowsComponents.Children.Single();
        updates.IsSelected = true;
        editor.SelectedNode = updates;
        editor.SelectedPolicy = editor.Policies.Single(p => p.Name == "Konfiguruj aktualizacje automatyczne");
        editor.Editor!.IsEnabled = true;
        editor.Editor.Elements.OfType<DropDownEditorViewModel>().Single().SelectedIndex = 2;
        editor.Editor.Elements.OfType<NumberEditorViewModel>().Single().Text = "3";
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Equal(1, editor.ChangedCount);
        Assert.Empty(editor.Editor.Problems);
        await ScreenshotWriter.SaveAsync(window, AdministrativeTemplates, dark: false);
    });

    [Fact]
    public Task Screenshot31_GroupPolicyModel() => ShootAsync(ThemePreference.Light, connect: false, shoot: async context =>
    {
        // K5 (design note docs/dev/k-computers-and-gpo.md 18): the model of WAW-PC001 at the site Warszawa, with an
        // administrative template policy configured by two GPOs, the minimum password length selected.
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        context.Directory.Session.ComputesTokenGroups = true;
        foreach (var (path, content) in SampleAdministrativeTemplates.Files())
        {
            context.Sysvol.PutPolicyDefinition(path, content);
        }

        var hidePreview = SampleAdministrativeTemplates.LoadPolish().Find(SampleAdministrativeTemplates.HidePreview)!.Policy;
        var domain = new RegistryPolicyDocument();
        PolicyEngine.Write(hidePreview, PolicySetting.Disabled, domain);
        context.Sysvol.Put(sample.DefaultDomainPolicy, "Machine/Registry.pol", domain.ToBytes());
        var workstations = new RegistryPolicyDocument();
        PolicyEngine.Write(hidePreview, PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>()), workstations);
        context.Sysvol.Put(sample.WorkstationPasswords, "Machine/Registry.pol", workstations.ToBytes());

        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(window, MainWindowWidth, MainWindowHeight);
        var shell = window.Shell;
        await shell.InitializeAsync();
        var model = Assert.IsType<GroupPolicyModelingDocumentViewModel>(shell.OpenGroupPolicyDocument(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Modeling) { ComputerDn = context.Company.Computer1, Name = "WAW-PC001" }));
        await UiWait.UntilAsync(() => !model.IsLoading && model.HasModel, "the model of WAW-PC001");
        model.SelectedSite = model.Sites.Single(s => s.Name == "Warszawa");
        await model.RecalculateCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !model.IsLoading && model.HasModel && !model.IsStale, "the model at the site Warszawa");
        model.ComputerPart.SelectedSetting = model.ComputerPart.Settings.Single(s => s.Name == "Minimalna długość hasła");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Contains(model.ComputerPart.Gpos, g => g.Name == "Lokacja Warszawa – Drukarki");
        Assert.Contains(model.ComputerPart.Settings, s => s.OverriddenCount == 1 && s.Area.StartsWith("Szablony administracyjne", StringComparison.Ordinal));
        await ScreenshotWriter.SaveAsync(window, GroupPolicyModel, dark: false);
    });

    [Fact]
    public Task Screenshot30_CentralStoreUpdate()
    {
        var files = new PreparedSaveDialog();
        return ShootAsync(ThemePreference.Light, connect: false, configure: s => s.AddSingleton<IFileDialogService>(files), shoot: async context =>
        {
            // K3: the update window comparing a package of templates (a ZIP: Polish files added, one template newer) with the store.
            context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
            foreach (var (path, content) in SampleAdministrativeTemplates.Files(polish: false))
            {
                context.Sysvol.PutPolicyDefinition(path, content);
            }

            var temp = System.IO.Directory.CreateTempSubdirectory("admng-k3-shot-");
            try
            {
                var package = Path.Combine(temp.FullName, "Szablony Contoso 1.3.zip");
                using (var zip = System.IO.Compression.ZipFile.Open(package, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (var (path, content) in SampleAdministrativeTemplates.Files())
                    {
                        using var entry = zip.CreateEntry(path).Open();
                        await entry.WriteAsync(path == "contoso.admx"
                            ? SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx.Replace("revision=\"1.2\"", "revision=\"1.3\"", StringComparison.Ordinal))
                            : content);
                    }
                }

                files.NextOpenPath = package;
                await context.ConnectAsync();
                UiTestSupport.ShowOffScreen(context.Services.GetRequiredService<MainWindow>());
                var factory = context.Services.GetRequiredService<GroupPolicyViewModelFactory>();
                var update = new CentralStoreUpdateViewModel(context.Services.GetRequiredService<GroupPolicyServices>(), factory.Definitions, factory.Loader)
                {
                    BackupFolder = @"\\pliki\Kopie\Magazyn centralny",
                };
                var window = (CentralStoreUpdateWindow)Windows(context).CreateWindow(update);
                await ScreenshotWriter.ShowWithClientSizeAsync(window, 900, 680);
                await update.OpenFileCommand.ExecuteAsync(null);
                await UiWait.UntilAsync(() => !update.IsWorking && update.Rows.Count == 6, "the comparison");
                await UiTestSupport.WaitForIdleAsync(rounds: 3);

                Assert.Null(update.Error);
                Assert.Equal("Szablony Contoso 1.3.zip – szablony: 2, języki: 2", update.PackageText);
                Assert.Equal("Do zapisania: 3, bez zmian: 3, pominięte: 0", update.SummaryText);
                Assert.True(window.UpdateButton.IsEnabled);
                await ScreenshotWriter.SaveAsync(window, CentralStoreUpdate, dark: false);
            }
            finally
            {
                temp.Delete(recursive: true);
            }
        });
    }

    [Fact]
    public Task Screenshot21_CompareObjects() => ShootAsync(ThemePreference.Light, connect: false, shoot: async context =>
    {
        // The same person in two domains, side by side (design note docs/dev/multi-window.md 10.6).
        await ComparisonFixture.AddFabrikamAsync(context);
        context.Connection.ProfileName = "Contoso";
        context.Connection.PreferredColor = ConnectionColor.Blue;
        await UiTestContext.ConnectAsync(context.Services, FakeDirectoryBuilder.DefaultDomain, "jan.kowalski");
        var other = context.CreateConnection();
        var otherConnection = other.GetRequiredService<ConnectionContext>();
        otherConnection.ProfileName = "Fabrikam";
        otherConnection.PreferredColor = ConnectionColor.Green;
        await UiTestContext.ConnectAsync(other, ComparisonFixture.Fabrikam, "anna.zielinska");

        // The main window of contoso owns the comparison window (and gives it its chrome).
        UiTestSupport.ShowOffScreen(context.Services.GetRequiredService<MainWindow>());
        var comparison = Factory(context).CreateObjectComparison(context.Company.LukaszWisniewski);
        var window = (CompareObjectsWindow)Windows(context).CreateWindow(comparison);
        await ScreenshotWriter.ShowWithClientSizeAsync(window, 1180, 780);
        await UiWait.InitializedAsync(window.Initialization);
        await UiWait.UntilAsync(() => comparison.HasResult && !comparison.IsRunning && !comparison.IsNestedLoading, "the comparison");
        comparison.SelectedAttributeRow = comparison.AttributeRows.Single(r => r.LdapName == Attr.Title);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Equal([Attr.City, Attr.Title], comparison.AttributeRows.Select(r => r.LdapName).Order(StringComparer.Ordinal));
        Assert.Equal("Członkostwo (2)", comparison.MembershipTabHeader);
        Assert.StartsWith("Porównanie: Łukasz Wiśniewski – contoso.test ↔ fabrikam.test", window.Title, StringComparison.Ordinal);
        await ScreenshotWriter.SaveAsync(window, CompareObjects, dark: false);
    });

    [Fact]
    public Task Screenshot23_GroupPolicy() => ShootAsync(ThemePreference.Light, connect: false, shoot: async context =>
    {
        // K2 (design note docs/dev/k-computers-and-gpo.md 10): "Zasady grupy" of OU=Komputery, the GPO node expanded.
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(window, MainWindowWidth, MainWindowHeight);
        var shell = window.Shell;
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(context.Company.ComputersOu));
        await shell.Tree.ExpandToAsync(context.Company.ComputersOu);
        var gpoRoot = shell.Tree.GroupPolicyObjectsNode!;
        gpoRoot.IsExpanded = true;
        await gpoRoot.EnsureChildrenLoadedAsync();

        shell.OpenContainerPoliciesCommand.Execute(context.Company.ComputersOu);
        var scope = Assert.IsType<GroupPolicyScopeDocumentViewModel>(shell.SelectedTab);
        await UiWait.UntilAsync(() => !scope.IsLoading && scope.Links.Count == 2 && scope.Inheritance.Count > 0, "the Group Policy of OU=Komputery");
        scope.SelectedLink = scope.Links.Single(l => l.Link.GpoId == sample.WorkstationPasswords);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Equal(7, gpoRoot.Children.Count);
        Assert.Contains(scope.Links, l => !l.Enabled);
        Assert.Contains(scope.Inheritance, r => r.GpoName == "Serwery – Zabezpieczenia" && r.EnforcedText == "Tak");
        await ScreenshotWriter.SaveAsync(window, GroupPolicy, dark: false);
    });

    [Fact]
    public Task Screenshot20_TwoConnections() => ShootAsync(ThemePreference.Light, connect: false, shoot: async context =>
    {
        // Two connection windows, two domains, two accounts (design note docs/dev/multi-window.md 10.6).
        var fabrikamBuilder = new FakeDirectoryBuilder("fabrikam.test");
        var fabrikamCompany = fabrikamBuilder.AddSampleCompany();
        context.Connector.AddDomain(fabrikamBuilder.Build());
        context.Connector.NonOwning = true;

        context.Connection.ProfileName = "Contoso";
        context.Connection.PreferredColor = ConnectionColor.Blue;
        await UiTestContext.ConnectAsync(context.Services, FakeDirectoryBuilder.DefaultDomain, "jan.kowalski");
        var other = context.CreateConnection();
        var otherConnection = other.GetRequiredService<ConnectionContext>();
        otherConnection.ProfileName = "Fabrikam";
        otherConnection.PreferredColor = ConnectionColor.Green;
        await UiTestContext.ConnectAsync(other, "fabrikam.test", "anna.zielinska");

        var left = context.Services.GetRequiredService<MainWindow>();
        var right = other.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(left, 1100, 700);
        await ScreenshotWriter.ShowWithClientSizeAsync(right, 1100, 700);
        await left.Shell.InitializeAsync();
        await right.Shell.InitializeAsync();
        await left.Shell.NavigateAsync(ListLocation.ForContainer(context.Company.ItOu));
        await left.Shell.Tree.ExpandToAsync(context.Company.ItOu);
        await right.Shell.NavigateAsync(ListLocation.ForContainer(fabrikamCompany.SalesOu));
        await right.Shell.Tree.ExpandToAsync(fabrikamCompany.SalesOu);
        await UiWait.UntilAsync(
            () => left.Shell.SelectedDocument?.IsFullyLoaded == true && right.Shell.SelectedDocument?.IsFullyLoaded == true,
            "the lists of both windows");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.StartsWith(@"contoso.test – CONTOSO\jan.kowalski", left.Title, StringComparison.Ordinal);
        Assert.StartsWith(@"fabrikam.test – FABRIKAM\anna.zielinska", right.Title, StringComparison.Ordinal);
        await ScreenshotWriter.SaveComposedAsync(left, right, TwoConnections, dark: false);
    });

    [Fact]
    public Task Screenshot22_Computers() => ShootAsync(ThemePreference.Light, async context =>
    {
        // The computer inventory sample (K1): DCs, servers with and without owners, workstations of every catalogue row.
        var sample = context.Directory.AddComputerInventorySample(context.Company);
        var factory = Computers.ComputerUiSupport.FactoryWithComputerDialogs(context.Services);
        var window = (AdMng.App.Computers.ComputerReviewWindow)Windows(context).CreateWindow(factory.CreateReview());
        await ScreenshotWriter.ShowAsync(window);
        await UiWait.InitializedAsync(window.Initialization);
        var review = window.ViewModel;
        await review.RunCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !review.IsBusy, "the computer review");
        window.ParametersExpander.IsExpanded = false;
        window.TilesList.SelectedItem = review.Tiles.Single(t => t.Report == Core.Computers.ComputerReport.EndOfSupport);
        await UiWait.UntilAsync(() => window.ItemsList.Items.Count > 0, "the computers past the end of support");
        window.ItemsList.SelectedItem = review.Items.Single(r => r.Dn == sample.Windows7);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Contains(review.Items, r => r.Name == "SRV-STARY04");
        await ScreenshotWriter.SaveAsync(window, ComputerReview, dark: false);
    });

    [Fact]
    public Task Screenshot01_MainWindow_LightTheme() =>
        ShootAsync(ThemePreference.Light, context => ShootMainWindowAsync(context, MainWindowLight, dark: false));

    [Fact]
    public Task Screenshot02_MainWindow_DarkTheme() =>
        ShootAsync(ThemePreference.Dark, context => ShootMainWindowAsync(context, MainWindowDark, dark: true));

    [Fact]
    public Task Screenshot03_UserProperties_GeneralTab() =>
        ShootAsync(ThemePreference.Light, context => ShootUserPropertiesAsync(context, L.Pages.General, PropertiesGeneral));

    [Fact]
    public Task Screenshot04_UserProperties_AccountTab() =>
        ShootAsync(ThemePreference.Light, context => ShootUserPropertiesAsync(context, L.Pages.Account, PropertiesAccount));

    [Fact]
    public Task Screenshot14_LogonHours() => ShootAsync(ThemePreference.Light, async context =>
    {
        // Warsaw in winter (UTC+1), so the time-zone note does not depend on the build agent.
        var warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var winter = new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);
        var hours = LogonHours.FromLocalGrid(
            LogonHoursGrid.Create((day, hour) =>
                (day is >= DayOfWeek.Monday and <= DayOfWeek.Friday && hour is >= 7 and < 18) || (day == DayOfWeek.Saturday && hour is >= 8 and < 13)),
            LogonHours.OffsetHours(warsaw, winter));
        var dialog = new LogonHoursViewModel(context.Services.GetRequiredService<ILocalizer>(), hours, warsaw, winter, "Jan Kowalski");
        dialog.BeginSelection(dialog.Days[0].Cells[7], extend: false);
        dialog.ExtendSelection(dialog.Days[4].Cells[17]);
        Assert.Equal("pon–pt 7–18; sob 8–13", dialog.Summary);

        var window = (LogonHoursWindow)Windows(context).CreateWindow(dialog);
        await ScreenshotWriter.ShowAsync(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        await ScreenshotWriter.SaveAsync(window, LogonHoursDialog, dark: false);
    });

    [Fact]
    public Task Screenshot05_BatchEditor_Preview() => ShootAsync(ThemePreference.Light, async context =>
    {
        var editor = Factory(context).CreateBatchEditor(new BatchEditorRequest(ItUsers(context))
        {
            Operations =
            [
                new SetAttributeOperation(Attr.Department, "IT i cyfryzacja"),
                new SetAttributeOperation(Attr.Office, "Warszawa, ul. Złota 44, piętro 12"),
            ],
            OpenPreview = true,
            Title = "Reorganizacja działu IT",
        });
        var window = (BatchEditorWindow)Windows(context).CreateWindow(editor);
        await ScreenshotWriter.ShowAsync(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiWait.UntilAsync(() => editor.Step == BatchEditorStep.Preview && editor.PreviewRows.Count > 0, "the preview of the planned changes");
        Assert.Contains(editor.PreviewRows, r => r.Name == "Jan Kowalski" && r.Before == "IT" && r.After == "IT i cyfryzacja");

        await ScreenshotWriter.SaveAsync(window, BatchPreview, dark: false);
    });

    [Fact]
    public Task Screenshot06_RecycleBin() => ShootAsync(ThemePreference.Light, async context =>
    {
        var company = context.Company;
        var session = context.Directory.Session;
        foreach (var dn in new[] { company.ZbigniewKazmierczak, company.ZanetaSzymanska, company.Laptop, company.Computer2, company.GroupSales })
        {
            await session.DeleteAsync(dn, subtree: false, CancellationToken.None);
        }

        var window = (RecycleBinWindow)Windows(context).CreateWindow(Factory(context).CreateRecycleBin());
        await ScreenshotWriter.ShowAsync(window);
        await UiWait.InitializedAsync(window.Initialization);
        await window.ViewModel.Loading;
        await UiWait.UntilAsync(() => window.ItemsList.Items.Count == 5, "the five deleted objects");
        window.ItemsList.SelectedItem = window.ViewModel.Items.Single(r => r.Name == "Żaneta Szymańska");

        await ScreenshotWriter.SaveAsync(window, RecycleBin, dark: false);
    });

    [Fact]
    public Task Screenshot17_AccountReview() => ShootAsync(ThemePreference.Light, async context =>
    {
        // Expiring and expired passwords (domain maximum age 90 days), an inactive and a never used account.
        var company = context.Company;
        var session = context.Directory.Session;
        var now = context.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        static string FileTime(DateTimeOffset value) => AdConvert.ToFileTime(value).ToString(CultureInfo.InvariantCulture);
        session.EmulateConstructedPasswordExpiry = true;
        session.MaxPasswordAge = TimeSpan.FromDays(90);
        var users = new DirectorySearch { BaseDn = company.CompanyOu, Filter = "(sAMAccountType=805306368)", Attributes = [Attr.Name] };
        await foreach (var user in session.SearchAsync(users, CancellationToken.None))
        {
            // Relative to the day the screenshot is taken: recent passwords and logons, then the findings below.
            session.SetValues(user.DistinguishedName, Attr.PwdLastSet, FileTime(now.AddDays(-10)));
            session.SetValues(user.DistinguishedName, Attr.LastLogonTimestamp, FileTime(now.AddDays(-2)));
        }

        session.SetValues(company.LukaszWisniewski, Attr.PwdLastSet, FileTime(now.AddDays(-84)));
        session.SetValues(company.MalgorzataWojcik, Attr.PwdLastSet, FileTime(now.AddDays(-79)));
        session.SetValues(company.BartlomiejKozlowski, Attr.PwdLastSet, FileTime(now.AddDays(-95)));
        session.SetValues(company.PawelLewandowski, Attr.LastLogonTimestamp, FileTime(now.AddDays(-131)));

        var window = (AccountReviewWindow)Windows(context).CreateWindow(Factory(context).CreateAccountReview());
        await ScreenshotWriter.ShowAsync(window);
        await UiWait.InitializedAsync(window.Initialization);
        await window.ViewModel.RunCommand.ExecuteAsync(null);
        window.ParametersExpander.IsExpanded = false;
        window.TilesList.SelectedItem = window.ViewModel.Tiles.Single(t => t.Check == AccountCheck.PasswordExpiring);
        await UiWait.UntilAsync(() => window.ItemsList.Items.Count == 2, "the two accounts whose password expires soon");
        window.ItemsList.SelectedItem = window.ViewModel.Items[0];

        await ScreenshotWriter.SaveAsync(window, AccountReview, dark: false);
    });

    [Fact]
    public Task Screenshot19_PrivilegedAccess() => ShootAsync(ThemePreference.Light, async context =>
    {
        // The sample company with privileged and unusual accounts (Polish names of the built-in groups).
        context.Directory.AddPrivilegedAccessSample(context.Company);
        var window = (AccountReviewWindow)Windows(context).CreateWindow(Factory(context).CreateAccountReview());
        await ScreenshotWriter.ShowAsync(window);
        await UiWait.InitializedAsync(window.Initialization);
        window.ViewModel.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
        await window.ViewModel.RunCommand.ExecuteAsync(null);
        var tab = window.ViewModel.Privileges;
        await UiWait.UntilAsync(() => !tab.IsRunning && tab.Items.Count > 0, "the privileged accounts");
        window.PrivilegesList.SelectedItem = tab.Items.Single(r => r.Name == "Henryk Żuławski");
        await UiWait.UntilAsync(() => tab.Findings.Count > 0 && window.FindingsList.Items.Count == tab.Findings.Count, "the findings of Henryk Żuławski");

        await ScreenshotWriter.SaveAsync(window, PrivilegedAccess, dark: false);
    });

    [Fact]
    public Task Screenshot18_UserMessages() => ShootAsync(ThemePreference.Light, async context =>
    {
        // Passwords that expire soon (domain maximum age 90 days), one already expired, one "must change".
        var company = context.Company;
        var session = context.Directory.Session;
        var now = context.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        static string FileTime(DateTimeOffset value) => AdConvert.ToFileTime(value).ToString(CultureInfo.InvariantCulture);
        session.EmulateConstructedPasswordExpiry = true;
        session.MaxPasswordAge = TimeSpan.FromDays(90);
        session.SetValues(company.JanKowalski, Attr.PwdLastSet, FileTime(now.AddDays(-80)));
        session.SetValues(company.LukaszWisniewski, Attr.PwdLastSet, FileTime(now.AddDays(-84)));
        session.SetValues(company.GrazynaDabrowska, Attr.PwdLastSet, FileTime(now.AddDays(-87)));
        session.SetValues(company.MalgorzataWojcik, Attr.PwdLastSet, FileTime(now.AddDays(-95)));
        session.SetValues(company.BartlomiejKozlowski, Attr.PwdLastSet, "0");
        await context.Services.GetRequiredService<ISettingsStore>().UpdateAsync(s =>
        {
            s.UserMessages.Smtp.Host = "smtp.contoso.com";
            s.UserMessages.Cc = "helpdesk@contoso.com";
        }, TestContext.Current.CancellationToken);

        var window = (UserMessagesWindow)Windows(context).CreateWindow(Factory(context).CreateUserMessageComposer(new Presentation.UserMessages.UserMessageRequest(
            [company.JanKowalski, company.LukaszWisniewski, company.GrazynaDabrowska, company.MalgorzataWojcik, company.BartlomiejKozlowski],
            Core.Messaging.BuiltInMessageTemplates.PasswordExpiring,
            14)));
        await ScreenshotWriter.ShowAsync(window);
        await UiWait.InitializedAsync(window.Initialization);
        await window.ViewModel.PlanCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => window.ItemsList.Items.Count == 5, "the five planned messages");
        window.ItemsList.SelectedItem = window.ViewModel.AllItems.First(i => i.Name == "Łukasz Wiśniewski");

        // U1c: "Widok" (WebView2, a native window a WPF render cannot capture) is the first tab – the picture shows the text.
        window.MessageTabs.SelectedItem = window.MessageTextTab;
        await ScreenshotWriter.SaveAsync(window, UserMessages, dark: false);
    }, configure: services => services.Replace(ServiceDescriptor.Singleton<Core.Messaging.IDirectSmtpTransportFactory>(new AdMng.Testing.FakeUserMessageTransport())));

    [Fact]
    public Task Screenshot07_Find_WithResults() => ShootAsync(ThemePreference.Light, async context =>
    {
        var find = Factory(context).CreateFind(context.Company.CompanyOu);
        var window = (FindWindow)Windows(context).CreateWindow(find);
        await ScreenshotWriter.ShowAsync(window);
        await UiWait.InitializedAsync(window.Initialization);

        find.Kind = find.Kinds.Single(k => k.Value == FindKind.Users);
        find.AddConditionCommand.Execute(null);
        window.AdvancedExpander.IsExpanded = true;
        var condition = Assert.Single(find.Conditions);
        condition.Field = condition.Fields.Single(f => f.Value == Attr.City);
        condition.Value = "Warszawa";
        await find.FindNowCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !find.Results.IsLoading && find.Results.Items.Count >= 3, "the users in Warsaw");
        Assert.Contains(find.Results.Items, r => r.Name == "Małgorzata Wójcik");

        await ScreenshotWriter.SaveAsync(window, Find, dark: false);
    });

    [Fact]
    public Task Screenshot08_ConnectWindow() => ShootAsync(ThemePreference.Light, connect: false, shoot: async context =>
    {
        var window = context.Services.GetRequiredService<ConnectWindow>();
        await ScreenshotWriter.ShowAsync(window);

        // Explicit credentials: the "current user" mode would show the build agent's account.
        var connect = window.ViewModel;
        connect.Authentication = AuthenticationMode.Credentials;
        connect.UserName = @"CONTOSO\jan.kowalski";
        await connect.FindDomainControllersCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => connect.DomainControllers.Count == 2, "the domain controllers of contoso.test");
        connect.SelectedDomainController = connect.DomainControllers[0];

        await ScreenshotWriter.SaveAsync(window, Connect, dark: false);
    });

    [Fact]
    public Task Screenshot09_Watchdog_Events() => ShootWatchdogAsync(WatchdogEvents, async (tab, view) =>
    {
        view.EventsView.EventsList.SelectedItem = tab.Events.Items.Single(r => r.Id == SampleEvents.LockoutId);
        await UiWait.UntilAsync(() => tab.Events.Details.Event?.Id == SampleEvents.LockoutId, "the details of the lockout");
        await tab.Events.Details.Loading;
    });

    [Fact]
    public Task Screenshot10_Watchdog_EventDetails() => ShootWatchdogAsync(WatchdogEventDetails, async (tab, view) =>
    {
        view.EventsView.EventsList.SelectedItem = tab.Events.Items.Single(r => r.Id == SampleEvents.PrivilegeGrantedId);
        await UiWait.UntilAsync(() => tab.Events.Details.Event?.Id == SampleEvents.PrivilegeGrantedId, "the details of the privileged access");
        await tab.Events.Details.Loading;
        Assert.NotEmpty(tab.Events.Details.Deliveries);
    });

    [Fact]
    public Task Screenshot11_Watchdog_Rules() => ShootWatchdogAsync(WatchdogRules, async (tab, view) =>
    {
        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Rules);
        tab.Rules.SelectedRule = tab.Rules.Rules.Single(r => r.Id == "lockouts");
        await UiWait.UntilAsync(() => view.RulesView.EditorView.ViewModel?.Id == "lockouts", "the editor of the lockout rule");
    });

    [Fact]
    public Task Screenshot12_Watchdog_Channels() => ShootWatchdogAsync(WatchdogChannels, async (tab, _) =>
    {
        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Channels);
        tab.Channels.SelectedChannel = tab.Channels.Channels.Single(c => c.Id == "siem");
    });

    [Fact]
    public Task Screenshot13_Watchdog_Status() => ShootWatchdogAsync(WatchdogStatus, async (tab, _) =>
    {
        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Status);
        await UiWait.UntilAsync(() => tab.Status.HasStatus && tab.Status.DeliveryLog.Items.Count > 0 && tab.Status.Audit.Count > 0, "the status");
    }, client => client.Status = SampleStatus.CreateLinux());

    [Fact]
    public Task Screenshot15_Watchdog_CertificateConfirmation() => ShootAsync(ThemePreference.Light, async context =>
    {
        var dialog = new WatchdogCertificateTrustViewModel(
            context.Services.GetRequiredService<ILocalizer>(), SampleStatus.Certificate, null, "admng-wd.contoso.test:8443", SampleEvents.Now,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));
        var window = (WatchdogCertificateTrustWindow)Windows(context).CreateWindow(dialog);
        await ScreenshotWriter.ShowAsync(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.StartsWith("3F2A 9C1E", window.FingerprintText.Text, StringComparison.Ordinal);

        await ScreenshotWriter.SaveAsync(window, WatchdogCertificate, dark: false);
    }, connect: false);

    [Fact]
    public Task Screenshot16_Watchdog_Sources() => ShootWatchdogAsync(WatchdogSources, async (tab, _) =>
    {
        await WatchdogUiSupport.SelectAsync(tab, WatchdogSubTab.Sources);
        await UiWait.UntilAsync(() => tab.Sources.SourceOptions.Count > 0, "the sources settings");
    });

    /// <summary>
    /// The main window with the Watchdog tab over the sample watchdog client (Polish data, Admin), the AD session of the
    /// sample company open; <paramref name="setup"/> adjusts the client (e.g. the status of the Linux service), <paramref name="arrange"/>
    /// picks the sub-tab and the selection.
    /// </summary>
    private static Task ShootWatchdogAsync(ScreenshotInfo shot, Func<WatchdogDocumentViewModel, WatchdogView, Task> arrange, Action<FakeWatchdogClient>? setup = null)
    {
        var client = WatchdogUiSupport.CreateClient();
        setup?.Invoke(client);
        return ShootAsync(ThemePreference.Light, configure: WatchdogUiSupport.Use(client), shoot: async context =>
        {
            await WatchdogUiSupport.ConfigureAddressAsync(context);
            var window = context.Services.GetRequiredService<MainWindow>();
            await ScreenshotWriter.ShowWithClientSizeAsync(window, MainWindowWidth, MainWindowHeight);
            await window.Shell.InitializeAsync();
            var (tab, view) = await WatchdogUiSupport.OpenConnectedAsync(window);
            await arrange(tab, view);
            await UiTestSupport.WaitForIdleAsync(rounds: 3);

            await ScreenshotWriter.SaveAsync(window, shot, dark: false);
        });
    }

    /// <summary>
    /// Runs one screenshot on the UI thread: a fresh host in Polish (dialogs scripted, see <see cref="TestDialogService"/>)
    /// with the sample company and a few more IT staff, optionally connected, in <paramref name="theme"/>. The theme is
    /// process wide, so it is reset to the system theme afterwards.
    /// </summary>
    private static Task ShootAsync(ThemePreference theme, Func<UiTestContext, Task> shoot, bool connect = true, Action<IServiceCollection>? configure = null)
    {
        ScreenshotWriter.RequireOutputDirectory();
        return WpfTestHost.RunAsync(async () =>
        {
            var (created, _) = await TestDialogService.CreateContextAsync(Language, configure);
            await using var context = created;
            AddSampleData(context);
            if (connect)
            {
                await context.ConnectAsync();
            }

            var themes = context.Services.GetRequiredService<IThemeService>();
            themes.Apply(theme);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            try
            {
                await shoot(context);
                context.AssertHealthy();
            }
            finally
            {
                UiTestSupport.CloseAllWindows();
                await UiTestSupport.WaitForIdleAsync(rounds: 2);
                themes.Apply(ThemePreference.System);
                await UiTestSupport.WaitForIdleAsync(rounds: 2);
            }
        });
    }

    /// <summary>The IT unit with its users listed, the tree expanded to it and Jan Kowalski selected (details pane).</summary>
    private static async Task ShootMainWindowAsync(UiTestContext context, ScreenshotInfo shot, bool dark)
    {
        var window = context.Services.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(window, MainWindowWidth, MainWindowHeight);
        var shell = window.Shell;
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(context.Company.ItOu));
        await shell.Tree.ExpandToAsync(context.Company.ItOu);
        var document = shell.SelectedDocument!;
        var users = ItUsers(context).Count;
        await UiWait.UntilAsync(() => document.IsFullyLoaded && document.Items.Count == users, "the users of OU=IT");

        var list = window.SelectedListView!;
        list.ListControl.UnselectAll();
        list.ListControl.SelectedItem = document.Items.Single(r => r.Name == "Jan Kowalski");
        await UiWait.UntilAsync(
            () => shell.SelectedObject?.Name == "Jan Kowalski" && window.DetailsContent.Visibility == Visibility.Visible,
            "the details of Jan Kowalski");

        await ScreenshotWriter.SaveAsync(window, shot, dark);
    }

    private static async Task ShootUserPropertiesAsync(UiTestContext context, string pageKey, ScreenshotInfo shot)
    {
        var window = (PropertiesWindow)Windows(context).CreateWindow(Factory(context).CreatePropertySheet(context.Company.JanKowalski));
        await ScreenshotWriter.ShowAsync(window);
        await UiWait.InitializedAsync(window.Initialization);
        var sheet = window.ViewModel;
        Assert.Null(sheet.LoadError);
        sheet.SelectedPage = sheet.Pages.Single(p => p.Key == pageKey);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Same(sheet.SelectedPage, window.PageTabs.SelectedItem);

        await ScreenshotWriter.SaveAsync(window, shot, dark: false);
    }

    /// <summary>
    /// More realistic content than the bare sample company: a fuller profile of Jan Kowalski (General and Account tabs)
    /// and the <see cref="ItStaff"/> in the IT unit, reporting to him.
    /// </summary>
    private static void AddSampleData(UiTestContext context)
    {
        var directory = context.Directory;
        var session = directory.Session;
        var jan = context.Company.JanKowalski;
        session.SetValues(jan, Attr.Description, "Administrator domeny i serwerów plików");
        session.SetValues(jan, Attr.Office, "Warszawa, pokój 204");
        session.SetValues(jan, Attr.TelephoneNumber, "+48 22 555 12 34");
        session.SetValues(jan, Attr.Initials, "JK");
        session.SetValues(jan, Attr.WwwHomePage, "https://intranet.contoso.test/it");
        session.SetValues(jan, Attr.AccountExpires,
            AdConvert.ToFileTime(new DateTimeOffset(2027, 3, 31, 22, 0, 0, TimeSpan.Zero)).ToString(CultureInfo.InvariantCulture));

        foreach (var person in ItStaff)
        {
            var flags = person.Disabled ? UserAccountControl.NormalAccount | UserAccountControl.AccountDisable : UserAccountControl.NormalAccount;
            directory.AddUser(person.GivenName, person.Surname, context.Company.ItOu, new Dictionary<string, object?>
            {
                [Attr.Department] = "IT",
                [Attr.Title] = person.Title,
                [Attr.City] = person.City,
                [Attr.Company] = "Contoso Sp. z o.o.",
                [Attr.Mail] = $"{person.Mailbox}@{directory.DomainDnsName}",
                [Attr.Manager] = jan,
                [Attr.UserAccountControl] = (int)flags,
            });
        }
    }

    /// <summary>DNs of all users of OU=IT (sample company plus <see cref="ItStaff"/>).</summary>
    private static List<string> ItUsers(UiTestContext context)
    {
        var it = DistinguishedName.Parse(context.Company.ItOu);
        return
        [
            context.Company.JanKowalski,
            context.Company.LukaszWisniewski,
            .. ItStaff.Select(p => it.Child("CN", $"{p.GivenName} {p.Surname}").ToString()),
        ];
    }

    private static IDialogViewModelFactory Factory(UiTestContext context) => context.Services.GetRequiredService<IDialogViewModelFactory>();

    private static IWindowService Windows(UiTestContext context) => context.Services.GetRequiredService<IWindowService>();

    private sealed record ItPerson(string GivenName, string Surname, string Title, string City, string Mailbox, bool Disabled = false);
}
