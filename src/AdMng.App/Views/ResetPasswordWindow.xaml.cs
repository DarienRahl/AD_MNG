using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Dialogs;

namespace AdMng.App.Views;

/// <summary>
/// Reset Password (modal). The password boxes are handed to the view model as <see cref="PasswordBoxInput"/>
/// adapters; closing while generated passwords were neither shown, copied nor exported asks first
/// (<see cref="ResetPasswordViewModel.CanCloseAsync"/>, also for the title bar button).
/// </summary>
public partial class ResetPasswordWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelHost _host;

    public ResetPasswordWindow(ResetPasswordViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);

        // Read only when the reset runs; never bound as strings.
        viewModel.PasswordInput = new PasswordBoxInput(PasswordBox);
        viewModel.ConfirmPasswordInput = new PasswordBoxInput(ConfirmPasswordBox);

        _host = WindowViewModelHost.Attach(this, viewModel);
        Loaded += OnLoaded;
    }

    public ResetPasswordViewModel ViewModel { get; }

    public bool? Result => _host.Result;

    /// <summary>Completes when the accounts were read (tests).</summary>
    internal Task Initialization => _host.Initialization;

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.PasswordInput = null;
        ViewModel.ConfirmPasswordInput = null;
        PasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (ViewModel.CanTypePassword)
        {
            PasswordBox.Focus();
        }
        else
        {
            ResetButton.Focus();
        }
    }
}
