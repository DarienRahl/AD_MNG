using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using AdMng.Presentation.Dialogs;

namespace AdMng.App.Infrastructure;

/// <summary>
/// Hosts an <see cref="IWindowViewModel"/> in its window as the contract requires:
/// <list type="bullet">
/// <item><see cref="IWindowViewModel.InitializeAsync"/> runs once the window is loaded (after Show / ShowDialog);</item>
/// <item><see cref="IWindowViewModel.CloseRequested"/> closes the window and keeps the result (<see cref="Result"/>);</item>
/// <item>a close from the title bar, Alt+F4 or <c>Close()</c> is cancelled, <see cref="IWindowViewModel.CanCloseAsync"/>
/// is awaited after the Closing event (WPF throws when Close() is called while a window is closing) and the window
/// closes for real only when it returns true;</item>
/// <item>the view model is disposed after the window closed.</item>
/// </list>
/// </summary>
internal sealed class WindowViewModelHost
{
    private readonly Window _window;
    private readonly IWindowViewModel _viewModel;
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closeConfirmed;
    private bool _confirmingClose;
    private bool _closed;

    private WindowViewModelHost(Window window, IWindowViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        _viewModel.CloseRequested += OnCloseRequested;
        _window.Loaded += OnLoaded;
        _window.Closing += OnClosing;
        _window.Closed += OnClosed;
    }

    /// <summary>True = confirmed, false = cancelled, null = closed without a result (title bar, owner closed).</summary>
    public bool? Result { get; private set; }

    /// <summary>Completes when <see cref="IWindowViewModel.InitializeAsync"/> has completed (tests wait for it).</summary>
    public Task Initialization => _initialized.Task;

    /// <summary>Wires <paramref name="viewModel"/> to <paramref name="window"/>. Call from the window constructor.</summary>
    public static WindowViewModelHost Attach(Window window, IWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        return new WindowViewModelHost(window, viewModel);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window.Loaded -= OnLoaded;
        try
        {
            // Never throws by contract; should it, the exception reaches the global exception handler.
            await _viewModel.InitializeAsync().ConfigureAwait(true);
        }
        finally
        {
            _initialized.TrySetResult();
        }
    }

    private void OnCloseRequested(object? sender, bool? result)
    {
        if (_closed)
        {
            return;
        }

        Result = result;
        _closeConfirmed = true;
        if (_confirmingClose)
        {
            // A confirmation of a title bar close is still pending: it closes the window when it completes.
            return;
        }

        _window.Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (e.Cancel || _closeConfirmed)
        {
            return;
        }

        // The view model decides (pending changes, generated passwords...) - after this event, never inside it.
        e.Cancel = true;
        if (!_confirmingClose)
        {
            _confirmingClose = true;
            _ = _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, ConfirmClose);
        }
    }

    private async void ConfirmClose()
    {
        try
        {
            if (_closed)
            {
                return;
            }

            var canClose = _closeConfirmed || await _viewModel.CanCloseAsync().ConfigureAwait(true);

            // The view model may have requested the close itself while the question was open.
            if ((canClose || _closeConfirmed) && !_closed)
            {
                _closeConfirmed = true;
                _confirmingClose = false;
                _window.Close();
            }
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _viewModel.CloseRequested -= OnCloseRequested;
        _window.Loaded -= OnLoaded;
        _window.Closing -= OnClosing;
        _window.Closed -= OnClosed;
        _initialized.TrySetResult();
        _viewModel.Dispose();
    }
}
