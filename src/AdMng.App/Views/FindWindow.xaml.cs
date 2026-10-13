using System.Windows;
using System.Windows.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Localization;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;

namespace AdMng.App.Views;

/// <summary>
/// Find (modeless). The results are an <see cref="ObjectListView"/> over <see cref="FindViewModel.Results"/>; activating
/// a row (double click / Enter) opens its properties through the view model (IDialogService). The list's shell context
/// menus need the main window's shell, so the window gives the list its own menu.
/// </summary>
public partial class FindWindow : Window
{
    private readonly WindowViewModelHost _host;

    public FindWindow(FindViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        ConfigureResultsMenus();
        _host = WindowViewModelHost.Attach(this, viewModel);
        Loaded += (_, _) => NameBox.Focus();
    }

    public FindViewModel ViewModel { get; }

    internal Task Initialization => _host.Initialization;

    private void ConfigureResultsMenus()
    {
        var properties = new MenuItem
        {
            Command = ViewModel.OpenCommand,
            FontWeight = FontWeights.SemiBold,
        };
        properties.SetBinding(HeaderedItemsControl.HeaderProperty, new TrBinding(L.Commands.Properties));
        var menu = new ContextMenu();
        menu.Items.Add(properties);
        ResultsView.ListControl.ContextMenu = menu;
        if (ResultsView.ListControl.View is GridView grid)
        {
            // Column chooser and export are shell commands (main window only).
            grid.ColumnHeaderContextMenu = null;
        }
    }

    private void OnResultActivated(object? sender, ObjectRowEventArgs e)
    {
        e.Handled = true;
        if (ViewModel.OpenCommand.CanExecute(e.Row))
        {
            ViewModel.OpenCommand.Execute(e.Row);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
