using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.Services;
using AdMng.Presentation.Services;

namespace AdMng.App.Views;

/// <summary>Info / error / confirmation dialog (see <see cref="MessageDialogViewModel"/>). Ctrl+C copies title and message.</summary>
public partial class MessageDialogWindow : Window, IDialogResultWindow
{
    private readonly MessageDialogViewModel _viewModel;

    public MessageDialogWindow(MessageDialogViewModel viewModel, IClipboardService clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        ConfigureButtons();
        KindGlyph.SetResourceReference(TextBlock.ForegroundProperty, viewModel.Kind switch
        {
            MessageKind.Error => "SystemFillColorCriticalBrush",
            MessageKind.Warning => "SystemFillColorCautionBrush",
            _ => "AccentTextFillColorPrimaryBrush",
        });

        // Ctrl+C anywhere but in the typed confirmation box (which copies its own selection) copies the message.
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => clipboard.SetText(viewModel.CopyText)));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Copy, Key.C, ModifierKeys.Control));
        viewModel.CloseRequested += OnCloseRequested;
        Loaded += OnLoaded;
    }

    public bool? Result { get; private set; }

    public MessageDialogViewModel ViewModel => _viewModel;

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        base.OnClosed(e);
    }

    private void ConfigureButtons()
    {
        if (!_viewModel.HasCancel)
        {
            // Single OK button: Enter and Esc both close.
            ConfirmButton.IsDefault = true;
            ConfirmButton.IsCancel = true;
            ConfirmButton.Style = (Style)FindResource("Ad.AccentDialogButton");
            return;
        }

        CancelButton.IsCancel = true;
        if (_viewModel.IsDestructive && !_viewModel.RequiresTypedConfirmation)
        {
            // Destructive Yes/No: Enter declines, the operator must choose "Yes" deliberately.
            CancelButton.IsDefault = true;
            ConfirmButton.Style = (Style)FindResource("Ad.DangerDialogButton");
        }
        else
        {
            ConfirmButton.IsDefault = true;
            ConfirmButton.Style = (Style)FindResource(_viewModel.IsDestructive ? "Ad.DangerDialogButton" : "Ad.AccentDialogButton");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.RequiresTypedConfirmation)
        {
            TypedInput.Focus();
        }
        else if (CancelButton.IsDefault)
        {
            CancelButton.Focus();
        }
        else
        {
            ConfirmButton.Focus();
        }
    }

    private void OnCloseRequested(object? sender, bool confirmed)
    {
        Result = confirmed;
        Close();
    }
}
