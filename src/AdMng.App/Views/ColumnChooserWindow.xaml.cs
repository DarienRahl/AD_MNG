using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Lists;

namespace AdMng.App.Views;

/// <summary>"Add/Remove Columns" of an object list. Double-click moves an attribute between the lists.</summary>
public partial class ColumnChooserWindow : Window, IDialogResultWindow
{
    public ColumnChooserWindow(ColumnChooserViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        Loaded += (_, _) => FilterBox.Focus();
    }

    public ColumnChooserViewModel ViewModel { get; }

    public bool? Result { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = false;
        Close();
    }

    private void OnAvailableDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnderMouse(AvailableList, e) is ColumnChoice choice && ViewModel.AddCommand.CanExecute(choice))
        {
            ViewModel.AddCommand.Execute(choice);
        }
    }

    private void OnSelectedDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnderMouse(SelectedList, e) is ColumnChoice choice && ViewModel.RemoveCommand.CanExecute(choice))
        {
            ViewModel.RemoveCommand.Execute(choice);
        }
    }

    private static object? ItemUnderMouse(ItemsControl list, MouseButtonEventArgs e) =>
        e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is ListBoxItem item
            ? item.DataContext
            : null;
}
