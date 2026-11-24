using AdMng.Presentation.Dialogs;

namespace AdMng.Presentation.Tests.TestDoubles;

/// <summary>
/// The window of a view model, as <c>WindowViewModelLifecycle</c> hosts it in the program: a close by the operator - the title
/// bar, Alt+F4 - asks <see cref="IWindowViewModel.CanCloseAsync"/> and closes only on true; a close the view model asks for
/// (<see cref="IWindowViewModel.CloseRequested"/>) closes at once; a closed window disposes its view model. Tests of closing a
/// window go through it - the real close path - instead of calling <c>Dispose</c> themselves.
/// </summary>
internal sealed class TestWindowHost
{
    private readonly IWindowViewModel _viewModel;
    private Task<bool>? _confirming;

    public TestWindowHost(IWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        viewModel.CloseRequested += OnCloseRequested;
    }

    /// <summary>True once the window closed; the view model is disposed then.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>How many times the window disposed its view model (once, whatever closed it).</summary>
    public int Disposals { get; private set; }

    /// <summary>The argument of the view model's close request; null when the operator closed the window.</summary>
    public bool? Result { get; private set; }

    /// <summary>
    /// The operator closes the window with the title bar: the view model is asked - one question at a time, as the host
    /// cancels the Closing event and asks after it - and the window closes when it allows it. True when it closed.
    /// </summary>
    public Task<bool> CloseByTitleBarAsync()
    {
        if (IsClosed)
        {
            return Task.FromResult(true);
        }

        // A second close while the first one asks or waits is the same close (the host's _confirming flag).
        return _confirming is { IsCompleted: false } pending ? pending : _confirming = ConfirmAsync();
    }

    private async Task<bool> ConfirmAsync()
    {
        if (await _viewModel.CanCloseAsync().ConfigureAwait(true) && !IsClosed)
        {
            Close();
        }

        return IsClosed;
    }

    private void OnCloseRequested(object? sender, bool? result)
    {
        if (IsClosed)
        {
            return;
        }

        Result = result;
        Close();
    }

    private void Close()
    {
        IsClosed = true;
        _viewModel.CloseRequested -= OnCloseRequested;
        Disposals++;
        _viewModel.Dispose();
    }
}
