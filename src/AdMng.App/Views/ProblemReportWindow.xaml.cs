using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Diagnostics;

namespace AdMng.App.Views;

/// <summary>
/// "Pomoc → Zgłoś problem…" (<see cref="ProblemReportViewModel"/>): the value-free problem report of this connection window,
/// which the operator copies or saves to a file of their choice (nothing is stored automatically).
/// </summary>
public partial class ProblemReportWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public ProblemReportWindow(ProblemReportViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        Loaded += (_, _) => ReportBox.Focus();
    }

    public ProblemReportViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model finished loading.</summary>
    public Task Initialization => _lifecycle.Initialization;
}
