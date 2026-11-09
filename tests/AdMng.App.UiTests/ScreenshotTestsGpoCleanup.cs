using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.GroupPolicy.Cleanup;
using AdMng.Presentation.GroupPolicy.Cleanup;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// Release screenshot 35 of "Porządki w GPO" (K7, design note docs/dev/k-computers-and-gpo.md 21.6, 21.10): the cleanup
/// advisor over the GPO management sample and the cleanup sample, the duplicated setting selected with its details and
/// its one action. Polish UI, light theme, the in-memory directory and SYSVOL with the sample ADMX templates. Like
/// <see cref="ScreenshotTests"/> it runs only when <see cref="ScreenshotWriter.DirectoryVariable"/> is set; the class name
/// starts with "ScreenshotTests" so the CI step "Screenshots" takes it too.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ScreenshotTestsGpoCleanup
{
    private const double MainWindowWidth = 1440;
    private const double MainWindowHeight = 900;

    private static readonly ScreenshotInfo Cleanup = new(
        "35-gpo-cleanup.png",
        "Porządki w GPO – propozycje doradcy: puste i niepołączone obiekty GPO, zduplikowane ustawienie z działaniem „Usuń duplikat z tego GPO”, kandydat do scalenia",
        "GPO cleanup – the advisor's proposals: empty and unlinked GPOs, a duplicated setting with the action \"Remove the duplicate from this GPO\", a merge candidate");

    [Fact]
    public Task Screenshot35_GpoCleanup() => ShootAsync(async context =>
    {
        context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        var sample = context.Directory.AddGroupPolicyCleanupSample(context.Company, context.Sysvol);
        foreach (var (path, content) in SampleAdministrativeTemplates.Files())
        {
            context.Sysvol.PutPolicyDefinition(path, content);
        }

        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(window, MainWindowWidth, MainWindowHeight);
        var shell = window.Shell;
        await shell.InitializeAsync();
        var document = Assert.IsType<GpoCleanupDocumentViewModel>(shell.OpenGroupPolicyDocument(GpoCleanupEntry.Request));
        await UiWait.UntilAsync(() => !document.IsLoading && document.HasResult, "the cleanup analysis");
        document.SelectedRow = document.Rows.Single(r => r.Kind == GpoCleanupKind.DuplicateSetting && r.GpoId == sample.TemplatesBranch);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Contains(document.Rows, r => r.Kind == GpoCleanupKind.EmptyGpo && r.GpoId == sample.EmptyGpo);
        Assert.Contains(document.Rows, r => r.Kind == GpoCleanupKind.MergeCandidate && r.GpoId == sample.RemoteDesktop);
        Assert.True(document.ShowsRemoveSetting);
        await ScreenshotWriter.SaveAsync(window, Cleanup, dark: false);
    });

    private static Task ShootAsync(Func<UiTestContext, Task> shoot)
    {
        ScreenshotWriter.RequireOutputDirectory();
        return WpfTestHost.RunAsync(async () =>
        {
            var (created, _) = await TestDialogService.CreateContextAsync("pl");
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
}
