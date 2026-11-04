using AdMng.Presentation.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>
/// A text prompt of the Korespondencja window (App <c>TextPromptWindow</c>, modal): a message and one line or a multiline
/// box. <see cref="Result"/> is the text after OK, null when cancelled.
/// </summary>
public sealed partial class TextPromptViewModel : ObservableObject, IWindowViewModel
{
    private readonly string _title;

    public TextPromptViewModel(string title, string message, string initial, bool multiline)
    {
        _title = title ?? string.Empty;
        Message = message ?? string.Empty;
        Text = initial ?? string.Empty;
        IsMultiline = multiline;
    }

    public event EventHandler<bool?>? CloseRequested;

    public string WindowTitle => _title;

    public string Message { get; }

    public bool IsMultiline { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>The confirmed text, or null (cancelled).</summary>
    public string? Result { get; private set; }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task<bool> CanCloseAsync() => Task.FromResult(true);

    public void Dispose()
    {
    }

    [RelayCommand]
    private void Ok()
    {
        Result = Text;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, false);
    }
}
