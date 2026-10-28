using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Correspondence;

namespace AdMng.App.Correspondence;

/// <summary>A read-only report of Korespondencja (<see cref="ReportViewModel"/>, modal).</summary>
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
    }

    public ReportViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;
}
