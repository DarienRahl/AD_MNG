using System.Collections.Concurrent;
using System.Reflection;
using Xunit.v3;

[assembly: AdMng.Presentation.Tests.Infrastructure.UiThread]

namespace AdMng.Presentation.Tests.Infrastructure;

/// <summary>
/// Runs a test with a single-threaded UI synchronization context, like WPF: the continuations of the view models' awaits
/// (<c>ConfigureAwait(true)</c>) and of the test itself run one after another on one thread, so a test never observes a
/// view model between two assignments that WPF runs as one step on the UI thread. Background work (<c>Task.Run</c>, the
/// directory doubles) still runs on the pool. The test's code up to its first await that yields runs on the runner's
/// thread (xUnit invokes the method there); everything after it runs on the UI thread.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class UiThreadAttribute : BeforeAfterTestAttribute
{
    private static readonly AsyncLocal<UiThreadSynchronizationContext?> Current = new();

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        // xUnit invokes the test right after the before-attributes, on this thread and with this context current.
        var context = new UiThreadSynchronizationContext();
        Current.Value = context;
        SynchronizationContext.SetSynchronizationContext(context);
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (Current.Value is not { } context)
        {
            return;
        }

        Current.Value = null;
        context.Complete();
        if (context.Errors is [var first, ..])
        {
            throw new InvalidOperationException("An exception escaped a callback on the test's UI thread.", first);
        }
    }
}

/// <summary>The UI thread of <see cref="UiThreadAttribute"/>: one dedicated thread that runs posted callbacks in order.</summary>
internal sealed class UiThreadSynchronizationContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly Thread _thread;

    public UiThreadSynchronizationContext()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "Test UI thread" };
        _thread.Start();
    }

    /// <summary>Exceptions that escaped a callback on the UI thread (an async void method that threw).</summary>
    public IReadOnlyList<Exception> Errors => [.. _errors];

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException) when (_queue.IsAddingCompleted)
        {
            // The test is over: what still arrives (the rest of the runner's pipeline, late background work of a view
            // model) runs on the pool instead of being lost.
            ThreadPool.UnsafeQueueUserWorkItem(static s => RunLate(s.Callback, s.State), (Callback: d, State: state), preferLocal: false);
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (Thread.CurrentThread == _thread)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Post(
            _ =>
            {
                try
                {
                    d(state);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            },
            null);
        done.Wait();
        if (error is not null)
        {
            throw new InvalidOperationException("A callback sent to the test's UI thread failed.", error);
        }
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Ends the thread after the callbacks queued so far (later posts run on the pool).</summary>
    public void Complete() => _queue.CompleteAdding();

    private static void RunLate(SendOrPostCallback callback, object? state)
    {
        try
        {
            callback(state);
        }
        catch (Exception)
        {
            // Nobody waits for it any more; the test's outcome is decided.
        }
    }

    private void Pump()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            try
            {
                callback(state);
            }
            catch (Exception ex)
            {
                _errors.Enqueue(ex);
            }
        }
    }
}
