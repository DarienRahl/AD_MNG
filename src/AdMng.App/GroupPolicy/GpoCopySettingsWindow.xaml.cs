using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.GroupPolicy.Copy;

namespace AdMng.App.GroupPolicy;

/// <summary>"Kopiuj ustawienia" / "Scal obiekty GPO" (modal, K7).</summary>
public partial class GpoCopySettingsWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelHost _host;

    public GpoCopySettingsWindow(GpoCopySettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        _host = WindowViewModelHost.Attach(this, viewModel);
        Loaded += (_, _) => ItemsList.Focus();
    }

    public GpoCopySettingsViewModel ViewModel { get; }

    public bool? Result => _host.Result;
}
