using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Computers;

namespace AdMng.App.Computers;

/// <summary>"Uruchom ponownie…" (<see cref="RestartComputerViewModel"/>, modal, K1). The result is the view model's request.</summary>
public partial class RestartComputerWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public RestartComputerWindow(RestartComputerViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        Loaded += (_, _) => (viewModel.NeedsTypedName ? TypedNameBox : (UIElement)DelayBox).Focus();
    }

    public RestartComputerViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;
}
