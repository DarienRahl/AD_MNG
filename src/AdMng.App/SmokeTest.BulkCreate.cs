using AdMng.App.BulkCreate;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.Views;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App;

/// <summary>F3 (docs/dev/phase3-bulk-create.md 2.7): the bulk creation wizard in the smoke test.</summary>
internal static partial class SmokeTest
{
    /// <summary>
    /// Disconnected, no directory: the batch editor in create mode on <em>Nowi pracownicy</em> (step 1 rendered), then the
    /// check step with the hand-built computation of <see cref="BulkCreateSamples"/> (OK, warning and error rows, the
    /// generated columns) and the preview of its hand-built plan – no planner, no session, no execution. Steps 1–3 with the
    /// sample sheet loaded through <c>Source</c> are rendered by B3's UI tests (BulkCreateInputUiTests).
    /// </summary>
    private static async Task RenderBulkCreateAsync(MainWindow main, IServiceProvider services)
    {
        _ = main;
        var windows = services.GetRequiredService<IWindowService>();
        var factory = services.GetRequiredService<IDialogViewModelFactory>();
        var editor = factory.CreateBatchEditor(new BatchEditorRequest([]) { Create = new BulkCreateRequest(ObjectKind.User, BulkCreateSamples.ContainerDn) });
        if (!editor.IsCreateMode || editor.BulkCreate is not { } bulk)
        {
            throw new InvalidOperationException("The batch editor did not open in create mode.");
        }

        var window = windows.CreateWindow(editor);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync().ConfigureAwait(true);

        bulk.ShowSample(BulkCreateSamples.Computation(), BulkCreateSamples.Plan());
        await UiTestSupport.WaitForIdleAsync().ConfigureAwait(true);
        if (bulk.Check.Rows.Count != BulkCreateSamples.Rows.Length || bulk.Check.Columns.Count == 0)
        {
            throw new InvalidOperationException("The check step did not show the sample rows.");
        }

        await editor.PreviewCommand.ExecuteAsync(null).ConfigureAwait(true);
        await UiTestSupport.WaitForIdleAsync().ConfigureAwait(true);
        if (!editor.IsPreviewStep)
        {
            throw new InvalidOperationException($"The preview of the sample plan did not open: {editor.Error}");
        }
    }
}
