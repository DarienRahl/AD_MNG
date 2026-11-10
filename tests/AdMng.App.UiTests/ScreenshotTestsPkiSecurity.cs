using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.UiTests.Pki;
using AdMng.App.Views;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using AdMng.Testing.Pki;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// Release screenshots 38 and 39 of AD CS (P1 W6, design note docs/dev/p-certificate-services.md 2.10, decision 10): the
/// security review over the Polish PKI sample with the ESC1 template selected, and the strong mapping and NTAuth readiness
/// report after <i>Sprawdź certyfikaty LDAPS</i>. Polish UI, light theme, the in-memory directory on the sample's clock
/// (2026-10-13) and the fake TLS probe. Like <see cref="ScreenshotTests"/> they run only when
/// <see cref="ScreenshotWriter.DirectoryVariable"/> is set; the class name starts with "ScreenshotTests" so the CI step
/// "Screenshots" takes it too.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ScreenshotTestsPkiSecurity
{
    private const double MainWindowWidth = 1440;
    private const double MainWindowHeight = 900;

    private static readonly ScreenshotInfo Security = new(
        "38-pki-security.png",
        "Przegląd bezpieczeństwa AD CS – uwagi ESC pogrupowane według szablonu, urzędu i konta, szczegóły ESC1 z kontami, zastrzeżeniami i nawigacją „Otwórz szablon / urząd”",
        "AD CS security review – ESC findings grouped by template, CA and account, the details of ESC1 with the accounts, the notes and the \"Open template / CA\" navigation");

    private static readonly ScreenshotInfo Readiness = new(
        "39-pki-readiness.png",
        "Gotowość na silne mapowanie i NTAuth – słabe mapowania altSecurityIdentities, szablony bez rozszerzenia SID, urząd spoza NTAuth, certyfikaty LDAPS i KDC kontrolerów domeny",
        "Strong mapping and NTAuth readiness – weak altSecurityIdentities mappings, templates without the SID extension, a CA outside NTAuth, the LDAPS and KDC certificates of the domain controllers");

    [Fact]
    public Task Screenshot38_PkiSecurityReview() => ShootAsync(async (context, _) =>
    {
        context.Directory.AddPublicKeySample(context.Company);
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(window, MainWindowWidth, MainWindowHeight);
        var shell = window.Shell;
        await shell.InitializeAsync();
        var review = await PkiSecurityUiSupport.OpenReviewAsync(shell);
        review.SelectedRow = review.Rows.Single(r => r.TemplateCn == PublicKeyTemplates.Esc1);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Equal(15, review.TotalCount);
        Assert.DoesNotContain(review.Rows, r => r.TemplateCn is { } cn && PublicKeyTemplates.Safe.Contains(cn));
        await ScreenshotWriter.SaveAsync(window, Security, dark: false);
    });

    [Fact]
    public Task Screenshot39_PkiReadiness() => ShootAsync(async (context, probe) =>
    {
        context.Directory.AddPublicKeySample(context.Company);
        PkiSecurityUiSupport.PresentTheSampleCertificate(probe);
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(window, MainWindowWidth, MainWindowHeight);
        var shell = window.Shell;
        await shell.InitializeAsync();
        var readiness = await PkiSecurityUiSupport.OpenReadinessAsync(shell);
        await PkiSecurityUiSupport.CheckLdapsAsync(readiness);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Equal(PkiReadinessStatus.Ok, readiness.Groups.Single(g => g.Area == PkiReadinessArea.DomainControllerLdaps).Status);
        Assert.Equal(PkiReadinessStatus.Problem, readiness.Groups.Single(g => g.Area == PkiReadinessArea.Mappings).Status);
        await ScreenshotWriter.SaveAsync(window, Readiness, dark: false);
    });

    private static Task ShootAsync(Func<UiTestContext, FakeDomainControllerTlsProbe, Task> shoot)
    {
        ScreenshotWriter.RequireOutputDirectory();
        return WpfTestHost.RunAsync(async () =>
        {
            var probe = new FakeDomainControllerTlsProbe();
            var (created, _) = await TestDialogService.CreateContextAsync("pl", PkiSecurityUiSupport.Configure(probe));
            await using var context = created;
            var themes = context.Services.GetRequiredService<IThemeService>();
            themes.Apply(ThemePreference.Light);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            try
            {
                await shoot(context, probe);
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
