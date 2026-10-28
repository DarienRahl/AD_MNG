using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Correspondence;

namespace AdMng.App.Correspondence;

/// <summary>Korespondencja (<see cref="CorrespondenceViewModel"/>, modeless, one per connection window from the Tools menu).</summary>
public partial class CorrespondenceWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public CorrespondenceWindow(CorrespondenceViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
    }

    public CorrespondenceViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model was initialized (tests).</summary>
    public Task Initialization => _lifecycle.Initialization;
}
