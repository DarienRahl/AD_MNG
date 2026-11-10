using AdMng.App.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.Pki;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App;

/// <summary>P1 (docs/dev/p-certificate-services.md 2.10): the W6 documents in the smoke test.</summary>
internal static partial class SmokeTest
{
    /// <summary>
    /// W6: <i>Przegląd bezpieczeństwa AD CS</i> and <i>Gotowość na silne mapowanie i NTAuth</i> as tabs of the (disconnected)
    /// main window – both views with their "not connected" state (no check runs and no domain controller is probed without a
    /// session; a check over the fake directory is covered by the UI tests), on the PKI services of the window's scope.
    /// </summary>
    private static async Task RenderPkiSecurityAsync(MainWindow main, IServiceProvider services)
    {
        var shell = main.Shell;
        var pki = services.GetRequiredService<PkiServices>();
        foreach (var kind in (PkiDocumentKind[])[PkiDocumentKind.SecurityReview, PkiDocumentKind.Readiness])
        {
            var document = shell.OpenPkiDocument(new PkiDocumentRequest(kind))
                ?? throw new InvalidOperationException($"The PKI document {kind} did not open.");
            await document.LoadAsync().ConfigureAwait(true);
            await UiTestSupport.WaitForIdleAsync(rounds: 2).ConfigureAwait(true);
            if (!ReferenceEquals(document.Pki, pki))
            {
                throw new InvalidOperationException($"The PKI document {kind} does not use the services of its connection window.");
            }

            if (document.IsLoading || document.Error is null)
            {
                throw new InvalidOperationException($"The PKI document {kind} of a disconnected window should wait for a connection.");
            }
        }
    }
}
