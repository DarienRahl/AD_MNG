using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// Screenshot 34 of the GPO settings search (K7, design note docs/dev/k-computers-and-gpo.md 21.4, 21.10): the GPO
/// management sample and the cleanup sample (one of its GPOs given the workstation password policy, so that the password
/// settings are set in three GPOs), the sample templates in the central store, the query "password" ("hasł" in the Polish
/// UI: the setting names follow the UI language) – the password settings grouped with "set in 3 GPOs – values differ /
/// values match", one occurrence selected with its details. UI in the language of
/// <see cref="ScreenshotWriter.LanguageVariable"/>, light theme. Like <see cref="ScreenshotTests"/> it runs only when
/// <see cref="ScreenshotWriter.DirectoryVariable"/> is set; the class name starts with "ScreenshotTests" so the CI step
/// "Screenshots" takes it too.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ScreenshotTestsGpoSearch
{
    private static string Language => ScreenshotWriter.Language;
    private const double MainWindowWidth = 1440;
    private const double MainWindowHeight = 900;

    private static readonly ScreenshotInfo SettingsSearch = new(
        "34-gpo-settings-search.png",
        "Wyszukiwarka ustawień GPO – „hasł” we wszystkich obiektach GPO domeny: ustawienia zgrupowane, w ilu GPO są ustawione i czy ich wartości są zgodne, połączenia każdego GPO i szczegóły zaznaczonego wystąpienia",
        "GPO settings search – \"password\" in every GPO of the domain: the settings grouped, in how many GPOs they are set and whether their values agree, the links of each GPO and the details of the selected occurrence");

    [Fact]
    public Task Screenshot34_GpoSettingsSearch() => ShootAsync(async context =>
    {
        context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        var cleanup = context.Directory.AddGroupPolicyCleanupSample(context.Company, context.Sysvol);
        context.Sysvol.Put(cleanup.TemplatesBranch, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template(
            "[System Access]", "MinimumPasswordLength = 10", "PasswordComplexity = 1", "MaximumPasswordAge = 42")));
        foreach (var (path, content) in SampleAdministrativeTemplates.Files())
        {
            context.Sysvol.PutPolicyDefinition(path, content);
        }

        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(window, MainWindowWidth, MainWindowHeight);
        var shell = window.Shell;
        await shell.InitializeAsync();
        var query = InLanguage("hasł", "password");
        var document = Assert.IsType<GpoSettingsSearchDocumentViewModel>(shell.OpenGroupPolicyDocument(GpoSettingsSearchEntry.Request with { SearchText = query }));
        await UiWait.UntilAsync(() => !document.IsLoading && document.HasIndex && !document.IsSummaryShown, $"the search for \"{query}\"");
        await document.SearchCommand.ExecuteAsync(null);
        var length = document.Groups.Single(g => g.Name == InLanguage("Minimalna długość hasła", "Minimum password length"));
        document.SelectedRow = length.Rows.Single(r => r.GpoName == "Stacje robocze – Zasady haseł");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Null(document.Error);
        Assert.Equal(3, length.GpoCount);
        Assert.Equal(GpoValueAgreement.Different, length.Agreement);
        Assert.Contains(document.Groups, g => g.Agreement == GpoValueAgreement.Same && g.GpoCount == 3);
        Assert.Empty(context.Sysvol.Writes);
        await ScreenshotWriter.SaveAsync(window, SettingsSearch, dark: false);
    });

    private static Task ShootAsync(Func<UiTestContext, Task> shoot)
    {
        ScreenshotWriter.RequireOutputDirectory();
        return WpfTestHost.RunAsync(async () =>
        {
            var (created, _) = await TestDialogService.CreateContextAsync(Language);
            await using var context = created;
            var themes = context.Services.GetRequiredService<IThemeService>();
            themes.Apply(ThemePreference.Light);
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

    /// <summary>The Polish or the English text, by the language of the screenshots.</summary>
    private static string InLanguage(string polish, string english) => Language == "pl" ? polish : english;
}
