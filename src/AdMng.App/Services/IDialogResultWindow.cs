namespace AdMng.App.Services;

/// <summary>
/// A dialog window that reports how it was closed without relying on <see cref="System.Windows.Window.DialogResult"/>
/// (which only works for modal windows; tests and the smoke test show dialogs modelessly).
/// </summary>
public interface IDialogResultWindow
{
    /// <summary>True = confirmed/saved, false = cancelled, null = closed with the window's close button.</summary>
    bool? Result { get; }
}
