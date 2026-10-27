using System.ComponentModel;
using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Changes;

namespace AdMng.App.Views;

/// <summary>
/// The change plan preview (modal): every GPO operation is previewed and executed here (design note
/// docs/dev/k-computers-and-gpo.md 3.3). After an execution the results tab is selected.
/// </summary>
public partial class ChangePlanPreviewWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelHost _host;

    public ChangePlanPreviewWindow(ChangePlanPreviewViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        _host = WindowViewModelHost.Attach(this, viewModel);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public ChangePlanPreviewViewModel ViewModel { get; }

    public bool? Result => _host.Result;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChangePlanPreviewViewModel.Step))
        {
            return;
        }

        PreviewTabs.SelectedItem = ViewModel.IsResultsStep ? ResultsTab : ChangesTab;
    }
}
