using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.GroupPolicy;

namespace AdMng.App.GroupPolicy;

/// <summary>New GPO, copy, rename (modal).</summary>
public partial class NewGpoWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelHost _host;

    public NewGpoWindow(NewGpoViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        _host = WindowViewModelHost.Attach(this, viewModel);
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public NewGpoViewModel ViewModel { get; }

    public bool? Result => _host.Result;
}
