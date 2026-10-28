using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace AdMng.App.Services;

/// <summary>
/// The Korespondencja windows of the connection window (U5, design note 9.4): multi-file open, the text prompt
/// (<c>TextPromptWindow</c>), the report (<c>ReportWindow</c>), the folder picker and the Korespondencja window itself.
/// Every window is owned by the connection's windows; may be called from any thread.
/// </summary>
public sealed partial class DialogService : ICorrespondenceDialogs, ICorrespondenceLauncher
{
    /// <summary>The system open dialog with multi-select (empty when cancelled).</summary>
    public async Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<FileFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        IReadOnlyList<string> files = [];
        await OnUiThreadAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = title ?? string.Empty,
                Filter = FileDialogService.BuildFilter(filters),
                CheckFileExists = true,
                Multiselect = true,
            };
            var owner = _windows.GetOwner();
            if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true)
            {
                files = [.. dialog.FileNames];
            }

            return files.Count > 0;
        }).ConfigureAwait(true);
        return files;
    }

    /// <summary>The text prompt (null when cancelled).</summary>
    public async Task<string?> PromptTextAsync(string title, string message, string initial, bool multiline)
    {
        string? text = null;
        await OnUiThreadAsync(() =>
        {
            var prompt = new TextPromptViewModel(title, message, initial, multiline);
            ShowWindowModal(prompt);
            text = prompt.Result;
            return text is not null;
        }).ConfigureAwait(true);
        return text;
    }

    /// <summary>The read-only report with "Kopiuj" and "Zamknij" (modal).</summary>
    public Task ShowReportAsync(string title, string text) => OnUiThreadAsync(() =>
    {
        var report = new ReportViewModel(title, text, _services.GetRequiredService<IClipboardService>());
        ShowWindowModal(report);
        return true;
    });

    /// <summary>The system folder picker (null when cancelled).</summary>
    public Task<string?> PickFolderAsync(string title) => PickFolderAsync(title, initialFolder: null);

    /// <summary>
    /// The Korespondencja window: without objects ("Korespondencja (Outlook)…") one per connection window, activated when
    /// open; with objects or for mailbox owners a new window each time.
    /// </summary>
    public Task ShowCorrespondenceAsync(CorrespondenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OnUiThreadAsync(() =>
        {
            if (request.DistinguishedNames.Count == 0 && request.Start == CorrespondenceStart.Recipients)
            {
                _tools.ShowCorrespondence();
                return true;
            }

            ShowWindowModeless(_services.GetRequiredService<CorrespondenceViewModelFactory>().Create(request), singletonKey: null);
            return true;
        });
    }
}
