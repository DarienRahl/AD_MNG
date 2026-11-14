using AdMng.App.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.Pki;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App;

/// <summary>P1 (docs/dev/p-certificate-services.md 2.10): the PKI node and the W5 documents in the smoke test.</summary>
internal static partial class SmokeTest
{
    /// <summary>
    /// W5: <i>Usługi kluczy publicznych</i> (every tab of the overview, the health and LDAPS tabs included) and <i>Szablony
    /// certyfikatów</i> as tabs of the (disconnected) main window – without a session they say "not connected" and probe and
    /// read nothing; the documents over the fake directory are covered by the UI tests.
    /// </summary>
    private static async Task RenderPkiAsync(MainWindow main, IServiceProvider services)
    {
        var shell = main.Shell;
        if (shell.Tree.Pki is not { } factory || !ReferenceEquals(factory.Pki, services.GetRequiredService<PkiServices>()))
        {
            throw new InvalidOperationException("The main window has no PKI services of its own connection.");
        }

        var overview = shell.OpenPkiDocument(new PkiDocumentRequest(PkiDocumentKind.Overview)) as PkiOverviewDocumentViewModel
            ?? throw new InvalidOperationException("The PKI overview did not open.");
        await overview.LoadAsync().ConfigureAwait(true);
        foreach (var tab in Enum.GetValues<PkiOverviewTab>())
        {
            overview.SelectedTab = tab;
            await UiTestSupport.WaitForIdleAsync().ConfigureAwait(true);
        }

        if (overview.HasSession || overview.IsProbingLdaps)
        {
            throw new InvalidOperationException("The PKI overview of a disconnected window should wait for a connection.");
        }

        var templates = shell.OpenPkiDocument(new PkiDocumentRequest(PkiDocumentKind.Templates))
            ?? throw new InvalidOperationException("The certificate templates did not open.");
        await templates.LoadAsync().ConfigureAwait(true);
        await UiTestSupport.WaitForIdleAsync(rounds: 2).ConfigureAwait(true);
    }
}
