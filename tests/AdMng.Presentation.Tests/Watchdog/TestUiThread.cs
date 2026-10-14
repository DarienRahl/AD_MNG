using System.Collections.Concurrent;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// A UI thread for view model tests: one dedicated thread with a <see cref="SynchronizationContext"/>, so the
/// continuations of the view models' awaits and everything dispatched through <see cref="IUiDispatcher"/> run one after
/// another on that thread - like WPF, unlike the inline dispatcher of the other tests. The Watchdog view models have
/// background loops (live polling) whose results must reach the UI thread in order. Exceptions that escape a posted
/// callback are recorded (<see cref="Errors"/>), never swallowed silently.
/// </summary>
internal sealed class TestUiThread : IUiDispatcher, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly Thread _thread;

    public TestUiThread()
    {
        Context = new UiSynchronizationContext(this);
        _thread = new Thread(Pump) { IsBackground = true, Name = "Watchdog test UI thread" };
        _thread.Start();
    }

    /// <summary>The synchronization context of the UI thread.</summary>
    public SynchronizationContext Context { get; }

    /// <summary>Exceptions that escaped a callback on the UI thread.</summary>
    public IReadOnlyList<Exception> Errors => [.. _errors];

    public bool CheckAccess() => Thread.CurrentThread == _thread;

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    /// <summary>Runs <paramref name="body"/> on the UI thread and completes when it (and its awaits) completed.</summary>
    public Task RunAsync(Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                await body();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    /// <summary>Runs <paramref name="body"/> on the UI thread and returns its result.</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        T result = default!;
        await RunAsync(async () => result = await body());
        return result;
    }

    /// <summary>Completes after everything queued on the UI thread so far (and what that queued) ran.</summary>
    public async Task IdleAsync(int rounds = 3)
    {
        for (var i = 0; i < rounds; i++)
        {
            await InvokeAsync(static () => { });
        }
    }

    public void Dispose() => _queue.CompleteAdding();

    private void Post(SendOrPostCallback callback, object? state = null)
    {
        try
        {
            _queue.Add((callback, state));
        }
        catch (InvalidOperationException) when (_queue.IsAddingCompleted)
        {
            // The test is over: late callbacks of background loops are dropped.
        }
    }

    private void Pump()
    {
        SynchronizationContext.SetSynchronizationContext(Context);
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

    private sealed class UiSynchronizationContext(TestUiThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => owner.Post(d, state);

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (owner.CheckAccess())
            {
                d(state);
                return;
            }

            owner.InvokeAsync(() => d(state)).GetAwaiter().GetResult();
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
