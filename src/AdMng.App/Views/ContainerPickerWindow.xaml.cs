using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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

    /// <summary>
    /// Scrolls the selected container into view - also when the view model selects it (the start container deep in a
    /// large domain). The item was usually just generated, so this waits for its layout.
    /// </summary>
    private void OnContainerSelected(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource) || sender is not TreeViewItem item)
        {
            return;
        }

        e.Handled = true;
        _ = Dispatcher.InvokeAsync(
            () =>
            {
                if (item.IsSelected && item.IsVisible)
                {
                    item.BringIntoView(new Rect(0, 0, item.ActualWidth, Math.Min(item.ActualHeight, 28)));
                }
            },
            DispatcherPriority.Loaded);
    }
}
