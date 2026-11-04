using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Correspondence;

namespace AdMng.App.Correspondence;

/// <summary>A read-only report of Korespondencja (<see cref="ReportViewModel"/>, modal): monospace text, "Kopiuj", "Zamknij".</summary>
public partial class ReportWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public ReportWindow(ReportViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        Loaded += (_, _) => CloseButton.Focus();
    }

    public ReportViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model was initialized (tests).</summary>
    public Task Initialization => _lifecycle.Initialization;
}
