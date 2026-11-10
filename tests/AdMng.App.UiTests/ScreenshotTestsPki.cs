using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using AdMng.Testing.Pki;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// Release screenshots 36 and 37 of AD CS (P1, docs/dev/p-certificate-services.md 2.10, decision 10): the PKI overview on its
/// health tab and the certificate templates with the enrollment rights of a deliberately vulnerable template, over the Polish
/// PKI sample. Polish UI, light theme. Like <see cref="ScreenshotTests"/> it runs only when
/// <see cref="ScreenshotWriter.DirectoryVariable"/> is set; the class name starts with "ScreenshotTests" so the CI step
/// "Screenshots" takes it too.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ScreenshotTestsPki
{
    private const double MainWindowWidth = 1440;
    private const double MainWindowHeight = 900;

    private static readonly ScreenshotInfo Overview = new(
        "36-pki-overview.png",
        "Usługi kluczy publicznych – zdrowie PKI w stylu pkiview: certyfikaty urzędów, lokalizacje AIA i CDP, listy CRL z terminami",
        "Public Key Services – pkiview-style PKI health: CA certificates, AIA and CDP locations, CRLs with their dates");

    private static readonly ScreenshotInfo Templates = new(
        "37-pki-templates.png",
        "Szablony certyfikatów – flagi jako zdania i kto naprawdę może się zarejestrować (liczba kont po rozwinięciu grup)",
        "Certificate templates – flags as sentences and who can really enroll (account counts after group expansion)");

    [Fact]
    public Task Screenshot36_PkiOverview() => ShootAsync(async context =>
    {
        context.Directory.AddPublicKeySample(context.Company);
        var probe = Assert.IsType<FakeDomainControllerTlsProbe>(context.Services.GetRequiredService<IDomainControllerTlsProbe>());
        probe.Set(PkiSamples.DcHost, 636, TlsProbeOutcome.Ok, PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer);
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(window, MainWindowWidth, MainWindowHeight);
        var shell = window.Shell;
        await shell.InitializeAsync();
        shell.Tree.PkiNode!.IsSelected = true;
        var document = Assert.IsType<PkiOverviewDocumentViewModel>(shell.SelectedTab);
        await UiWait.UntilAsync(() => !document.IsLoading && document.Snapshot is not null, "the PKI overview");
        document.SelectedTab = PkiOverviewTab.Health;
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.True(document.HasHealthRows);
        await ScreenshotWriter.SaveAsync(window, Overview, dark: false);
    });

    [Fact]
    public Task Screenshot37_PkiTemplates() => ShootAsync(async context =>
    {
        context.Directory.AddPublicKeySample(context.Company);
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(window, MainWindowWidth, MainWindowHeight);
        var shell = window.Shell;
        await shell.InitializeAsync();
        var document = Assert.IsType<PkiTemplatesDocumentViewModel>(
            shell.OpenPkiDocument(new PkiDocumentRequest(PkiDocumentKind.Templates) { TemplateCn = PublicKeyTemplates.Esc1 }));
        await UiWait.UntilAsync(() => !document.IsLoading && document.IsExpanded, "the templates and their account counts");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.True(document.SelectedRow?.IsBroadEnroll);
        await ScreenshotWriter.SaveAsync(window, Templates, dark: false);
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
