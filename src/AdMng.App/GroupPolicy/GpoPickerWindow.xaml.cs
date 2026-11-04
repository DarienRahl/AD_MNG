using System.Windows;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.GroupPolicy;

namespace AdMng.App.GroupPolicy;

/// <summary>Choose GPOs of the domain (modal).</summary>
public partial class GpoPickerWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelHost _host;

    public GpoPickerWindow(GpoPickerViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        _host = WindowViewModelHost.Attach(this, viewModel);
        Loaded += (_, _) => FilterBox.Focus();
    }

    public GpoPickerViewModel ViewModel { get; }

    public bool? Result => _host.Result;

    /// <summary>Completes when the GPOs were read (tests).</summary>
    internal Task Initialization => _host.Initialization;

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!ViewModel.MultiSelect && ViewModel.OkCommand.CanExecute(null))
        {
            ViewModel.OkCommand.Execute(null);
            e.Handled = true;
        }
    }
}
