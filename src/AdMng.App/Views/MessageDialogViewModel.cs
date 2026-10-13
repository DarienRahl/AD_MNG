using AdMng.App.Controls;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.App.Views;

/// <summary>Icon and colour of a message dialog.</summary>
public enum MessageKind
{
    Information,
    Warning,
    Error,
    Question,
}

/// <summary>
/// Information, error and confirmation dialogs (the shell's replacement for MessageBox: Fluent styled, localized
/// buttons, copyable text). A typed confirmation enables the confirm button only after the operator typed
/// <see cref="ExpectedText"/> exactly.
/// </summary>
public sealed partial class MessageDialogViewModel : ObservableObject
{
    private MessageDialogViewModel(string title, string message, MessageKind kind, string confirmText, string? cancelText)
    {
        Title = title ?? string.Empty;
        Message = message ?? string.Empty;
        Kind = kind;
        ConfirmText = confirmText;
        CancelText = cancelText;
    }

    /// <summary>Raised by <see cref="ConfirmCommand"/> (true) and <see cref="CancelCommand"/> (false).</summary>
    public event EventHandler<bool>? CloseRequested;

    public string Title { get; }

    public string Message { get; }

    public MessageKind Kind { get; }

    public string Glyph => Kind switch
    {
        MessageKind.Error => Glyphs.Error,
        MessageKind.Warning => Glyphs.Warning,
        MessageKind.Question => Glyphs.Question,
        _ => Glyphs.Info,
    };

    /// <summary>Text of the confirm (or only) button.</summary>
    public string ConfirmText { get; }

    /// <summary>Text of the cancel button; null for a single-button dialog.</summary>
    public string? CancelText { get; }

    public bool HasCancel => CancelText is not null;

    /// <summary>The confirm button performs a destructive action (red, and Enter/Esc both decline).</summary>
    public bool IsDestructive { get; private init; }

    /// <summary>Text the operator must type to confirm (null = no typed confirmation).</summary>
    public string? ExpectedText { get; private init; }

    public bool RequiresTypedConfirmation => ExpectedText is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string TypedText { get; set; } = string.Empty;

    /// <summary>Title and message for Ctrl+C.</summary>
    public string CopyText => $"{Title}{Environment.NewLine}{Environment.NewLine}{Message}";

    public static MessageDialogViewModel Information(string title, string message, ILocalizer localizer) =>
        new(title, message, MessageKind.Information, Text(localizer, L.Common.Ok), null);

    public static MessageDialogViewModel Error(string title, string message, ILocalizer localizer) =>
        new(title, message, MessageKind.Error, Text(localizer, L.Common.Ok), null);

    public static MessageDialogViewModel Confirmation(string title, string message, bool destructive, ILocalizer localizer) =>
        new(title, message, destructive ? MessageKind.Warning : MessageKind.Question, Text(localizer, L.Common.Yes), Text(localizer, L.Common.No))
        {
            IsDestructive = destructive,
        };

    public static MessageDialogViewModel TypedConfirmation(string title, string message, string expectedText, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(expectedText);
        return new(title, message, MessageKind.Warning, Text(localizer, L.Ui.Confirm), Text(localizer, L.Common.Cancel))
        {
            IsDestructive = true,
            ExpectedText = expectedText,
        };
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => CloseRequested?.Invoke(this, true);

    private bool CanConfirm() => ExpectedText is null || string.Equals(TypedText.Trim(), ExpectedText, StringComparison.Ordinal);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private static string Text(ILocalizer localizer, string key)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[key];
    }
}
