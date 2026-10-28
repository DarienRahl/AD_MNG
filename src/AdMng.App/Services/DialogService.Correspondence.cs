using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Services;

namespace AdMng.App.Services;

/// <summary>
/// The Korespondencja windows of the connection window (U5, design note 9.4): multi-file open, the text prompt
/// (<c>TextPromptWindow</c>), the report (<c>ReportWindow</c>), the folder picker and the Korespondencja window itself.
/// Scaffold: the App track implements the bodies.
/// </summary>
public sealed partial class DialogService : ICorrespondenceDialogs, ICorrespondenceLauncher
{
    /// <summary>The system open dialog with multi-select (empty when cancelled).</summary>
    public Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<FileFilter> filters) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>The text prompt (null when cancelled).</summary>
    public Task<string?> PromptTextAsync(string title, string message, string initial, bool multiline) => Task.FromResult<string?>(null);

    /// <summary>The read-only report with "Kopiuj" and "Zamknij".</summary>
    public Task ShowReportAsync(string title, string text) => Task.CompletedTask;

    /// <summary>The system folder picker (null when cancelled).</summary>
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    /// <summary>The Korespondencja window: one per connection window without recipients, else a new window.</summary>
    public Task ShowCorrespondenceAsync(CorrespondenceRequest request) => Task.CompletedTask;
}
