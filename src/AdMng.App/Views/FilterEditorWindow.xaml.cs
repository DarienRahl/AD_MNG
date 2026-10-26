using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Filters;

namespace AdMng.App.Views;

/// <summary>
/// The filter builder (<see cref="FilterEditorViewModel"/>, README U4): conditions in groups, raw LDAP, live preview,
/// test count. Modal; Save or Cancel closes it.
/// </summary>
public partial class FilterEditorWindow : Window, IDialogResultWindow
{
    public FilterEditorWindow(FilterEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        Loaded += (_, _) => NameBox.Focus();
    }

    public FilterEditorViewModel ViewModel { get; }

    public bool? Result { get; private set; }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.CloseRequested -= OnCloseRequested;
        if (ViewModel.TestCommand.IsRunning)
        {
            ViewModel.TestCancelCommand.Execute(null);
        }

        base.OnClosed(e);
    }

    private void OnCloseRequested(object? sender, bool saved)
    {
        Result = saved;
        Close();
    }
}
