using System.Windows;
using System.Windows.Threading;
using AdMng.App.Connections;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;
using Microsoft.Extensions.Logging;

namespace AdMng.App.Services;

/// <summary>
/// Last line of defence: unhandled exceptions on the UI thread and unobserved task exceptions are logged (type and
/// HRESULT only - messages may contain directory values) and shown as a localized error dialog; the application keeps
/// running. Only fatal conditions (out of memory) still terminate the process. In the smoke test a recorder replaces
/// the dialog. The dialog is shown by <see cref="IAppErrorPresenter"/> (the connection window manager): owned by the
/// active connection window, without a connection header (the exception's connection is unknown), one per connection.
/// </summary>
public sealed class GlobalExceptionHandler
{
    private readonly Dispatcher _dispatcher;
    private readonly IAppErrorPresenter _presenter;
    private readonly ErrorTranslator _errors;
    private readonly ILocalizer _localizer;
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private Application? _application;

    public GlobalExceptionHandler(
        WpfUiDispatcher dispatcher,
        IAppErrorPresenter presenter,
        ErrorTranslator errors,
        ILocalizer localizer,
        ILogger<GlobalExceptionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher.Dispatcher;
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>When set, exceptions are handed to this callback instead of a dialog (smoke test, UI tests).</summary>
    public Action<Exception>? Recorder { get; set; }

    public void Attach(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        Detach();
        _application = application;
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    public void Detach()
    {
        if (_application is { } application)
        {
            application.DispatcherUnhandledException -= OnDispatcherUnhandledException;
        }

        _application = null;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
    }

    /// <summary>Reports an exception caught elsewhere (e.g. a failed fire-and-forget UI operation).</summary>
    public void Report(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Log(exception, "Unhandled UI exception");
        if (Recorder is { } recorder)
        {
            recorder(exception);
            return;
        }

        _ = _dispatcher.InvokeAsync(() => ShowAsync(exception), DispatcherPriority.Background);
    }

    internal static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (IsFatal(e.Exception))
        {
            Log(e.Exception, "Fatal UI exception");
            return;
        }

        e.Handled = true;
        Report(e.Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        var exception = e.Exception.InnerExceptions.Count == 1 ? e.Exception.InnerExceptions[0] : e.Exception;
        if (exception is OperationCanceledException)
        {
            return;
        }

        Report(exception);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log(exception, "Unhandled exception (terminating)");
            Recorder?.Invoke(exception);
        }
    }

    private async Task ShowAsync(Exception exception)
    {
        // One dialog per connection window at a time (the presenter guards): a burst of identical failures must not stack
        // dialogs, and an error while another window's dialog is open is still shown.
        try
        {
            await _presenter.ShowErrorAsync(_localizer[L.Connections.ApplicationError], _errors.Translate(exception)).ConfigureAwait(true);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            Log(ex, "Showing the error dialog failed");
        }
    }

    private void Log(Exception exception, string what) =>
        _logger.LogError("{What}: {ExceptionType} (HRESULT 0x{HResult:X8}) at {Site}.",
            what, exception.GetType().FullName, exception.HResult, exception.TargetSite?.DeclaringType?.FullName ?? "?");
}
