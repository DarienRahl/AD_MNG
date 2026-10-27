using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.GroupPolicy;

namespace AdMng.App.GroupPolicy;

/// <summary>A new or edited WMI filter (modal).</summary>
public partial class WmiFilterEditorWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelHost _host;

    public WmiFilterEditorWindow(WmiFilterEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        _host = WindowViewModelHost.Attach(this, viewModel);
        Loaded += (_, _) => NameBox.Focus();
    }

    public WmiFilterEditorViewModel ViewModel { get; }

    public bool? Result => _host.Result;
}
