using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.UserMessages;

namespace AdMng.App.Views;

/// <summary>
/// Messages to users (<see cref="UserMessageComposerViewModel"/>, README U1). The SMTP password box is handed to the view
/// model as an <see cref="PasswordBoxInput"/> (read only when sending, then cleared); F5 prepares the preview.
/// </summary>
public partial class UserMessagesWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public UserMessagesWindow(UserMessageComposerViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        viewModel.SmtpPasswordInput = new PasswordBoxInput(SmtpPasswordBox);
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        Closed += (_, _) => SmtpPasswordBox.Clear();
        Loaded += (_, _) => (viewModel.Recipients.Count > 0 ? SubjectBox : (UIElement)AddRecipientsButton).Focus();
    }

    public UserMessageComposerViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model finished initializing.</summary>
    public Task Initialization => _lifecycle.Initialization;
}
