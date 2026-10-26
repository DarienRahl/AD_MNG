using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Settings;

namespace AdMng.App.Views;

/// <summary>
/// Settings dialog (<see cref="SettingsViewModel"/>, modal). Save writes the settings and applies language, theme and
/// Advanced Features immediately; <see cref="Result"/> is true after saving.
/// </summary>
public partial class SettingsWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        if (viewModel.Messages is { } messages)
        {
            messages.SmtpPasswordInput = new PasswordBoxInput(MessagesPasswordBox);
            Closed += (_, _) => MessagesPasswordBox.Clear();
        }

        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        Loaded += (_, _) => LanguageBox.Focus();
    }

    public SettingsViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model finished loading.</summary>
    public Task Initialization => _lifecycle.Initialization;
}
