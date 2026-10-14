using System.Globalization;
using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// Runs one screenshot on the UI thread: a fresh host in Polish (dialogs scripted, see <see cref="TestDialogService"/>)
    /// with the sample company and a few more IT staff, optionally connected, in <paramref name="theme"/>. The theme is
    /// process wide, so it is reset to the system theme afterwards.
    /// </summary>
    private static Task ShootAsync(ThemePreference theme, Func<UiTestContext, Task> shoot, bool connect = true)
    {
        ScreenshotWriter.RequireOutputDirectory();
        return WpfTestHost.RunAsync(async () =>
        {
            var (created, _) = await TestDialogService.CreateContextAsync(Language);
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
