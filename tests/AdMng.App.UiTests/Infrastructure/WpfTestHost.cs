using System.Windows;
using System.Windows.Threading;
using AdMng.App.Infrastructure;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// One dedicated STA thread with a running <see cref="Dispatcher"/> and the application object (App.xaml resources:
/// Fluent theme and the shell styles), shared by all UI tests of the process. Test bodies run on that thread one at a
/// time; WPF binding errors are recorded for the whole process and checked by each test.
/// </summary>
internal static class WpfTestHost
{
    private static readonly Lazy<HostThread> Instance = new(() => new HostThread(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Binding errors recorded since the last <see cref="BindingErrorListener.Clear"/>.</summary>
    public static BindingErrorListener Bindings => Instance.Value.Bindings;

    /// <summary>
    /// Runs <paramref name="body"/> on the UI thread (serialized with other UI tests). Skips the test outside Windows.
    /// </summary>
    public static Task RunAsync(Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF UI tests need Windows.");
        return Instance.Value.RunAsync(body);
    }

    private sealed class HostThread
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dispatcher _dispatcher;

        public HostThread()
        {
            using var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            BindingErrorListener? bindings = null;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    dispatcher = Dispatcher.CurrentDispatcher;
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                    bindings = BindingErrorListener.Install();
                    var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.InitializeComponent();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    ready.Set();
                }

                if (failure is null)
                {
                    Dispatcher.Run();
                }
            })
            {
                IsBackground = true,
                Name = "WPF UI test thread",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(60)))
            {
                throw new TimeoutException("The WPF UI test thread did not start.");
            }

            if (failure is not null)
            {
                throw new InvalidOperationException("Creating the WPF application failed.", failure);
            }

            _dispatcher = dispatcher!;
            Bindings = bindings!;
        }

        public BindingErrorListener Bindings { get; }

        public async Task RunAsync(Func<Task> body)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // A hung UI test fails after two minutes instead of blocking the whole run.
                var operation = _dispatcher.InvokeAsync(body);
                await operation.Task.Unwrap().WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
