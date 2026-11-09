using AdMng.App.Views;

namespace AdMng.App;

/// <summary>P1 (docs/dev/p-certificate-services.md 2.10): the PKI node and the W5 documents in the smoke test.</summary>
internal static partial class SmokeTest
{
    /// <summary>
    /// W5: <i>Usługi kluczy publicznych</i> (overview with the health and LDAPS tabs) and <i>Szablony certyfikatów</i> as tabs
    /// of the (disconnected) main window. The contract stub renders nothing.
    /// </summary>
    private static Task RenderPkiAsync(MainWindow main, IServiceProvider services) => Task.CompletedTask;
}
