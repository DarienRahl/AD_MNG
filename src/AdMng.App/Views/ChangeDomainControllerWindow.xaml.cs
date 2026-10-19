using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Dialogs;

namespace AdMng.App.Views;

/// <summary>
/// "Change Domain Controller" (modal). Closing the window while the new DC is being contacted cancels the switch
/// (<see cref="ChangeDomainControllerViewModel.CanCloseAsync"/>): the session stays on its DC.
/// </summary>
public partial class ChangeDomainControllerWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelHost _host;

    public ChangeDomainControllerWindow(ChangeDomainControllerViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        _host = WindowViewModelHost.Attach(this, viewModel);
        Loaded += (_, _) => DomainControllerList.Focus();
    }

    public ChangeDomainControllerViewModel ViewModel { get; }

    public bool? Result => _host.Result;

    /// <summary>Completes when the list of domain controllers was read (tests).</summary>
    internal Task Initialization => _host.Initialization;
}
