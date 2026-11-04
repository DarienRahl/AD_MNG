using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Computers;

namespace AdMng.App.Computers;

/// <summary>
/// "Zdalne akcje" of one computer (<see cref="RemoteComputerViewModel"/>, K1). Modeless, one per computer per connection
/// window; closing it cancels the running WinRM call. F5 reads the status again.
/// </summary>
public partial class RemoteComputerWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public RemoteComputerWindow(RemoteComputerViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        Loaded += (_, _) => (RefreshStatusButton.IsEnabled ? RefreshStatusButton : (UIElement)CloseButton).Focus();
    }

    public RemoteComputerViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model resolved the computer (tests).</summary>
    public Task Initialization => _lifecycle.Initialization;
}
