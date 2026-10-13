using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Views;

namespace AdMng.App.Views;

/// <summary>Editor of a saved view (name, search base, scope, LDAP filter with live validation and a test count).</summary>
public partial class SavedViewEditorWindow : Window, IDialogResultWindow
{
    public SavedViewEditorWindow(SavedViewEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        Loaded += (_, _) => NameBox.Focus();
    }

    public SavedViewEditorViewModel ViewModel { get; }

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
