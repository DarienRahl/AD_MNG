using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.Presentation.GroupPolicy;

namespace AdMng.App.GroupPolicy;

/// <summary>The comparison of two GPOs (modeless).</summary>
public partial class GpoComparisonWindow : Window
{
    private readonly WindowViewModelHost _host;

    public GpoComparisonWindow(GpoComparisonViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        _host = WindowViewModelHost.Attach(this, viewModel);

        // The columns of the two GPOs carry their names (a GridViewColumn has no DataContext to bind to).
        ColumnA.Header = viewModel.NameA;
        ColumnB.Header = viewModel.NameB;
    }

    public GpoComparisonViewModel ViewModel { get; }

    /// <summary>Completes when both GPOs were read and compared (tests).</summary>
    internal Task Initialization => _host.Initialization;
}
