using System.Windows;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Connection;

namespace AdMng.App.Views;

/// <summary>
/// Sign-in window (domain, domain controller, credentials, channel security, profiles). The password is handed to the
/// view model through <see cref="PasswordBoxInput"/> only when connecting and the box is cleared afterwards.
/// </summary>
public partial class ConnectWindow : Window
{
    public ConnectWindow(ConnectViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        viewModel.PasswordInput = new PasswordBoxInput(PasswordBox);
        viewModel.Connected += OnConnected;
        Loaded += OnLoaded;
    }

    /// <summary>Raised after a successful connection (the navigator then opens the main window).</summary>
    public event EventHandler? Connected;

    public ConnectViewModel ViewModel { get; }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.Connected -= OnConnected;

        // A connect still running when the window closes is abandoned (its session would have no window).
        if (ViewModel.ConnectCommand.IsRunning)
        {
            ViewModel.ConnectCancelCommand.Execute(null);
        }

        ViewModel.PasswordInput = null;
        PasswordBox.Clear();
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Initialize();

        // Start where typing is needed: the domain if empty, else the password / user name for explicit credentials.
        if (string.IsNullOrWhiteSpace(ViewModel.Domain))
        {
            DomainBox.Focus();
        }
        else if (ViewModel.RequiresCredentials && string.IsNullOrWhiteSpace(ViewModel.UserName))
        {
            UserNameBox.Focus();
        }
        else if (ViewModel.RequiresCredentials)
        {
            PasswordBox.Focus();
        }
        else
        {
            ConnectButton.Focus();
        }
    }

    private void OnConnected(object? sender, EventArgs e) => Connected?.Invoke(this, EventArgs.Empty);
}
