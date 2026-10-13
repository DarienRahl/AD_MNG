using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Dialogs;

namespace AdMng.App.Views;

/// <summary>
/// Session log (<see cref="SessionLogViewModel"/>): the changes of this session, newest first, updated live. Double-click
/// or Enter opens the object's properties, Ctrl+C copies its distinguished name.
/// </summary>
public partial class SessionLogWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public SessionLogWindow(SessionLogViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        Loaded += (_, _) => FilterBox.Focus();
    }

    public SessionLogViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model finished loading.</summary>
    public Task Initialization => _lifecycle.Initialization;

    private void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: AuditEntryViewModel entry } && ViewModel.OpenCommand.CanExecute(entry))
        {
            ViewModel.OpenCommand.Execute(entry);
            e.Handled = true;
        }
    }
}
