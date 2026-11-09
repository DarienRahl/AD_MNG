using AdMng.App.Views;

namespace AdMng.App;

/// <summary>F3 (docs/dev/phase3-bulk-create.md 2.7): the bulk creation wizard in the smoke test.</summary>
internal static partial class SmokeTest
{
    /// <summary>
    /// B4: disconnected, no directory – the batch editor in create mode with the synthetic TSV of
    /// <c>BulkCreate/BulkCreateSamples.cs</c> loaded through <c>Source</c>, mapped, steps 1–3 rendered, then the check step
    /// and the preview from a hand-built computation and plan (no planner, no session, no execution). The contract stub
    /// renders nothing.
    /// </summary>
    private static Task RenderBulkCreateAsync(MainWindow main, IServiceProvider services) => Task.CompletedTask;
}
