namespace AdMng.Presentation.Tests.Infrastructure;

/// <summary>
/// Listens for task exceptions that nobody observed while a test runs. The application reports each of them as an
/// "unexpected error" dialog (GlobalExceptionHandler), so work that a Stop abandons must not produce one when it fails
/// afterwards. The event is process-wide and other tests run in parallel: ask only for exceptions the test can recognise
/// (its own marker message, its own file name).
/// </summary>
internal sealed class UnobservedTaskExceptions : IDisposable
{
    private readonly List<Exception> _seen = [];

    public UnobservedTaskExceptions() => TaskScheduler.UnobservedTaskException += OnUnobserved;

    public void Dispose() => TaskScheduler.UnobservedTaskException -= OnUnobserved;

    /// <summary>
    /// Forces the finalizers that raise the event and waits until <paramref name="isMine"/> matches one of the exceptions
    /// seen, at most <paramref name="rounds"/> collections. True when one matched.
    /// </summary>
    public async Task<bool> CollectAsync(Func<Exception, bool> isMine, int rounds = 20)
    {
        for (var i = 0; i < rounds; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            lock (_seen)
            {
                if (_seen.Any(isMine))
                {
                    return true;
                }
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        lock (_seen)
        {
            _seen.AddRange(e.Exception.Flatten().InnerExceptions);
        }
    }
}
