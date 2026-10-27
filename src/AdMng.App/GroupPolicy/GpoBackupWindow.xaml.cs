using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.GroupPolicy;

namespace AdMng.App.GroupPolicy;

/// <summary>Backup, restore, import and the backup before a delete (modal).</summary>
public partial class GpoBackupWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelHost _host;

    public GpoBackupWindow(GpoBackupViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        _host = WindowViewModelHost.Attach(this, viewModel);
    }

    public GpoBackupViewModel ViewModel { get; }

    public bool? Result => _host.Result;

    /// <summary>Completes when the backups of the remembered folder were listed (tests).</summary>
    internal Task Initialization => _host.Initialization;
}
