using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Comparison;

namespace AdMng.App.Views;

/// <summary>
/// "Porównaj z obiektem w innym połączeniu…" (<see cref="ObjectComparisonViewModel"/>): side-by-side, read-only comparison
/// of an object of this connection with the matching object of another open connection. Modeless, one per launch, owned
/// by this connection's main window and carrying this connection's chrome.
/// </summary>
public partial class CompareObjectsWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public CompareObjectsWindow(ObjectComparisonViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
    }

    public ObjectComparisonViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model finished its first comparison (or found nothing to compare with).</summary>
    public Task Initialization => _lifecycle.Initialization;
}
