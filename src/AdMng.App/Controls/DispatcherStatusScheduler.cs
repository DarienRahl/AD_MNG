using System.Windows.Threading;

namespace AdMng.App.Controls;

/// <summary>
/// The clock of a status line in the application: a <see cref="DispatcherTimer"/> of the line's own dispatcher, at
/// normal priority (a delay that only runs when the thread is idle would show "Working" late exactly when the thread is
/// busy). One timer per call, stopped and released when it fires or is cancelled.
/// </summary>
internal sealed class DispatcherStatusScheduler(Dispatcher dispatcher) : IStatusScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = delay };
        var handle = new Handle(timer);
        timer.Tick += (_, _) =>
        {
            handle.Dispose();
            callback();
        };
        timer.Start();
        return handle;
    }

    private sealed class Handle(DispatcherTimer timer) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                timer.Stop();
            }
        }
    }
}
