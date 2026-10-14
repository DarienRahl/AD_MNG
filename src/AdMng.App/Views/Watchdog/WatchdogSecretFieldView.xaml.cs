using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>
/// A write-only secret field over <see cref="WatchdogSecretFieldViewModel"/>. Typing hands the text to
/// <see cref="WatchdogSecretFieldViewModel.SetNewValue"/>; the password box is emptied when the view model changes
/// (editor rebuilt after save or revert) or no longer holds a new value (Undo, Clear), and focused when the view model asks
/// (the destination changed: the secret must be entered again).
/// </summary>
public partial class WatchdogSecretFieldView : UserControl
{
    private WatchdogSecretFieldViewModel? _viewModel;
    private bool _clearing;

    public WatchdogSecretFieldView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) => Attach(DataContext as WatchdogSecretFieldViewModel);
    }

    /// <summary>The password box (tests type into it).</summary>
    public PasswordBox PasswordInput => Input;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        ClearInput();
        if (IsLoaded)
        {
            Attach(e.NewValue as WatchdogSecretFieldViewModel);
        }
    }

    private void Attach(WatchdogSecretFieldViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        Detach();
        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.FocusRequested += OnFocusRequested;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.FocusRequested -= OnFocusRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_clearing && DataContext is WatchdogSecretFieldViewModel viewModel)
        {
            viewModel.SetNewValue(Input.Password);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WatchdogSecretFieldViewModel.HasNewValue) && sender is WatchdogSecretFieldViewModel { HasNewValue: false })
        {
            ClearInput();
        }
    }

    private void OnFocusRequested(object? sender, EventArgs e) =>
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Input.BringIntoView();
            Input.Focus();
        });

    /// <summary>Empties the box without telling the view model (it already has no new value).</summary>
    private void ClearInput()
    {
        if (Input.Password.Length == 0)
        {
            return;
        }

        _clearing = true;
        try
        {
            Input.Clear();
        }
        finally
        {
            _clearing = false;
        }
    }
}
