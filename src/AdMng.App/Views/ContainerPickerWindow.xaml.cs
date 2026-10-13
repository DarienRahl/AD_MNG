using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Dialogs;

namespace AdMng.App.Views;

/// <summary>"Browse for Container" (modal): the domain tree, expanded to the start container when it loads.</summary>
public partial class ContainerPickerWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelHost _host;

    public ContainerPickerWindow(ContainerPickerViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        _host = WindowViewModelHost.Attach(this, viewModel);
        Loaded += (_, _) => ContainerTree.Focus();
    }

    public ContainerPickerViewModel ViewModel { get; }

    public bool? Result => _host.Result;

    /// <summary>Completes when the tree was expanded to the start container (tests).</summary>
    internal Task Initialization => _host.Initialization;
}
