using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using AdMng.Presentation.Dialogs;

namespace AdMng.App.Infrastructure;

/// <summary>
/// Hosts an <see cref="IWindowViewModel"/> in its window, as the contract of the interface describes:
/// <list type="bullet">
/// <item><see cref="IWindowViewModel.InitializeAsync"/> runs once when the window is loaded (modal and modeless alike);</item>
/// <item><see cref="IWindowViewModel.CloseRequested"/> closes the window and keeps the argument as <see cref="Result"/>;</item>
/// <item>a close by the operator (title bar, Alt+F4, <see cref="Window.Close"/>) is cancelled first, then
/// <see cref="IWindowViewModel.CanCloseAsync"/> is awaited after the Closing event and the window closes only when it
/// returns true (WPF throws when Close() or a modal dialog runs inside Closing);</item>
/// <item>the view model is disposed after the window closed.</item>
/// </list>
/// A close forced by the owner window or by application shutdown cannot be cancelled; it still disposes the view model.
/// </summary>
public sealed class WindowViewModelLifecycle
{
    private readonly Window _window;
    private readonly IWindowViewModel _viewModel;
    private readonly TaskCompletionSource _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closeApproved;
    private bool _confirming;
    private bool _closed;

    public WindowViewModelLifecycle(Window window, IWindowViewModel viewModel)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        window.Loaded += OnLoaded;
        window.Closing += OnClosing;
        window.Closed += OnClosed;
        viewModel.CloseRequested += OnCloseRequested;
    }

    /// <summary>Completes when <see cref="IWindowViewModel.InitializeAsync"/> finished (cancelled when the window closed before loading).</summary>
    public Task Initialization => _initialization.Task;

    /// <summary>The argument of the view model's close request; null when the window was closed by the operator.</summary>
    public bool? Result { get; private set; }

    /// <summary>True after the window closed (the view model is disposed).</summary>
    public bool IsClosed => _closed;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window.Loaded -= OnLoaded;
        try
        {
            await _viewModel.InitializeAsync().ConfigureAwait(true);
            _initialization.TrySetResult();
        }
        catch (Exception ex)
        {
            // InitializeAsync never throws by contract; if it does, the global handler reports it.
            _initialization.TrySetException(ex);
            throw;
        }
    }

    private void OnCloseRequested(object? sender, bool? result)
    {
        if (_closed)
        {
            return;
        }

        Result = result;
        _closeApproved = true;
        _window.Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (e.Cancel || _closeApproved)
        {
            return;
        }

        // Ask the view model after this event: it may show a confirmation, and the second Close() must not run while
        // the window is still closing.
        e.Cancel = true;
        if (!_confirming)
        {
            _confirming = true;
            _ = _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ConfirmClose));
        }
    }

    private async void ConfirmClose()
    {
        try
        {
            if (!_closed && await _viewModel.CanCloseAsync().ConfigureAwait(true) && !_closed)
            {
                _closeApproved = true;
                _window.Close();
            }
        }
        finally
        {
            _confirming = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _window.Loaded -= OnLoaded;
        _window.Closing -= OnClosing;
        _window.Closed -= OnClosed;
        _viewModel.CloseRequested -= OnCloseRequested;
        _initialization.TrySetCanceled();
        _viewModel.Dispose();
    }
}
