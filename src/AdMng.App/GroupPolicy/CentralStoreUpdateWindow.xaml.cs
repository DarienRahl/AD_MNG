using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

namespace AdMng.App.GroupPolicy;

/// <summary>"Zaktualizuj szablony ADMX" (modal, K3).</summary>
public partial class CentralStoreUpdateWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelHost _host;

    public CentralStoreUpdateWindow(CentralStoreUpdateViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        _host = WindowViewModelHost.Attach(this, viewModel);
    }

    public CentralStoreUpdateViewModel ViewModel { get; }

    public bool? Result => _host.Result;
}
