using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Dialogs;

namespace AdMng.App.Views;

/// <summary>
/// "Select Users, Contacts, Computers, or Groups" (modal). The results list hands its live multi-selection to the view
/// model (<see cref="ObjectPickerViewModel.SelectedResults"/>); a double click adds the object (and confirms a
/// single-object pick, like ADUC).
/// </summary>
public partial class ObjectPickerWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelHost _host;

    public ObjectPickerWindow(ObjectPickerViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        ResultsList.SelectionMode = viewModel.IsMultiSelect ? SelectionMode.Extended : SelectionMode.Single;
        viewModel.SelectedResults = ResultsList.SelectedItems;
        _host = WindowViewModelHost.Attach(this, viewModel);
        Loaded += (_, _) => SearchBox.Focus();
    }

    public ObjectPickerViewModel ViewModel { get; }

    public bool? Result => _host.Result;

    internal Task Initialization => _host.Initialization;

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.SelectedResults = null;
        base.OnClosed(e);
    }

    private void OnResultsSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.NotifySelectionChanged();

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListViewItem { DataContext: DirectoryItem })
        {
            return;
        }

        e.Handled = true;
        if (ViewModel.AddSelectedCommand.CanExecute(null))
        {
            ViewModel.AddSelectedCommand.Execute(null);
        }

        if (!ViewModel.IsMultiSelect && ViewModel.OkCommand.CanExecute(null))
        {
            ViewModel.OkCommand.Execute(null);
        }
    }
}
