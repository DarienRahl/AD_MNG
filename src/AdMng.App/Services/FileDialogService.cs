using AdMng.Presentation.Services;
using Microsoft.Win32;

namespace AdMng.App.Services;

/// <summary>Common Windows open/save dialogs, owned by the active window.</summary>
public sealed class FileDialogService(IWindowService windows) : IFileDialogService
{
    public Task<string?> OpenFileAsync(string title, IReadOnlyList<FileFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = BuildFilter(filters),
            CheckFileExists = true,
            Multiselect = false,
        };
        return Task.FromResult(Show(dialog) ? dialog.FileName : null);
    }

    public Task<string?> SaveFileAsync(string title, string defaultFileName, IReadOnlyList<FileFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = BuildFilter(filters),
            FileName = defaultFileName,
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = filters.Count > 0 ? DefaultExtension(filters[0].Pattern) : string.Empty,
        };
        return Task.FromResult(Show(dialog) ? dialog.FileName : null);
    }

    private bool Show(CommonDialog dialog) => (windows.GetOwner() is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;

    /// <summary>"CSV files (*.csv)|*.csv|..." - the description must not contain the separator.</summary>
    internal static string BuildFilter(IReadOnlyList<FileFilter> filters) =>
        string.Join('|', filters.Select(f => $"{f.Description.Replace('|', ' ')}|{f.Pattern}"));

    /// <summary>"*.csv" → ".csv"; patterns with several extensions or wildcards in the name give no default.</summary>
    internal static string DefaultExtension(string pattern) =>
        pattern.StartsWith("*.", StringComparison.Ordinal) && pattern.IndexOfAny([';', '*', '?'], 2) < 0 ? pattern[1..] : string.Empty;
}
