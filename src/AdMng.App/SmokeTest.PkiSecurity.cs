using AdMng.App.Views;

namespace AdMng.App;

/// <summary>P1 (docs/dev/p-certificate-services.md 2.10): the W6 documents in the smoke test.</summary>
internal static partial class SmokeTest
{
    /// <summary>
    /// W6: <i>Przegląd bezpieczeństwa AD CS</i> and <i>Gotowość na silne mapowanie i NTAuth</i> as tabs of the (disconnected)
    /// main window. The contract stub renders nothing.
    /// </summary>
    private static Task RenderPkiSecurityAsync(MainWindow main, IServiceProvider services) => Task.CompletedTask;
}
