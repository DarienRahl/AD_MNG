using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>
/// A read-only text report of the Korespondencja window (App <c>ReportWindow</c>: monospace text, "Kopiuj" and "Zamknij"):
/// diagnostics, the journal statistics, the help text.
/// </summary>
public sealed partial class ReportViewModel : ObservableObject, IWindowViewModel
{
    private readonly string _title;
    private readonly IClipboardService _clipboard;

    public ReportViewModel(string title, string text, IClipboardService clipboard)
    {
        _title = title ?? string.Empty;
        Text = text ?? string.Empty;
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    }

    public event EventHandler<bool?>? CloseRequested;

    public string WindowTitle => _title;

    public string Text { get; }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task<bool> CanCloseAsync() => Task.FromResult(true);

    public void Dispose()
    {
    }

    [RelayCommand]
    private void Copy() => _clipboard.SetText(Text);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, null);
}
