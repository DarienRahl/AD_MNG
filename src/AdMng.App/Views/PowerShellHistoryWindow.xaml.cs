using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Dialogs;

namespace AdMng.App.Views;

/// <summary>
/// PowerShell history (<see cref="PowerShellHistoryViewModel"/>): the changes of this session with the equivalent
/// ActiveDirectory-module script, which the operator can copy or save as .ps1 (nothing is stored automatically).
/// </summary>
public partial class PowerShellHistoryWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public PowerShellHistoryWindow(PowerShellHistoryViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        Loaded += (_, _) => ItemsList.Focus();
    }

    public PowerShellHistoryViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model finished loading.</summary>
    public Task Initialization => _lifecycle.Initialization;
}
