using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AdMng.Outlook;

/// <summary>
/// One background thread that owns the Outlook COM objects of a session (design note docs/dev/u5-outlook-correspondence.md
/// 4.1): <see cref="ApartmentState.STA"/> on Windows (MTA elsewhere, for the tests), a queue of work items run one at a time
/// in order, and on Windows the COM message filter <see cref="ComRetryFilter"/> (Outlook busy → retry after 150 ms for up to
/// 60 s, then cancel). A work item cancelled while it waits in the queue never runs; once it runs it completes (a COM call
/// cannot be interrupted). <see cref="DisposeAsync"/> lets the queued items run, revokes the filter and ends the thread.
/// </summary>
internal sealed class StaWorker : IAsyncDisposable
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(30);

    private readonly BlockingCollection<WorkItem> _queue = [];
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _useMessageFilter;
    private int _disposed;

    /// <param name="name">The thread name (diagnostics).</param>
    /// <param name="useMessageFilter">Register the COM message filter (default: on Windows).</param>
    public StaWorker(string name = "AdMng Outlook COM", bool? useMessageFilter = null)
    {
        _useMessageFilter = useMessageFilter ?? OperatingSystem.IsWindows();
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        if (OperatingSystem.IsWindows())
        {
            _thread.SetApartmentState(ApartmentState.STA);
        }

        _thread.Start();
    }

    /// <summary>The managed id of the worker thread.</summary>
    public int ThreadId => _thread.ManagedThreadId;

    /// <summary>The apartment of the worker thread (STA on Windows).</summary>
    public ApartmentState Apartment => _thread.GetApartmentState();

    /// <summary>The caller runs on the worker thread.</summary>
    public bool IsCurrentThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>Whether <see cref="DisposeAsync"/> was called.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Completes when the worker thread has ended.</summary>
    public Task Completion => _ended.Task;

    /// <summary>Runs <paramref name="work"/> on the worker thread after the items queued before it.</summary>
    /// <exception cref="ObjectDisposedException">The worker was disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before the item started.</exception>
    public Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        if (IsCurrentThread)
        {
            // Nested call from a work item: waiting for the queue would dead-lock.
            try
            {
                return Task.FromResult(work());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var item = new WorkItem<T>(work, cancellationToken);
        try
        {
            _queue.Add(item, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(StaWorker));
        }

        return item.Task;
    }

    /// <summary>Runs <paramref name="work"/> on the worker thread after the items queued before it.</summary>
    public Task RunAsync(Action work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync<object?>(
            () =>
            {
                work();
                return null;
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _queue.CompleteAdding();
        }

        if (IsCurrentThread)
        {
            return;
        }

        try
        {
            await _ended.Task.WaitAsync(JoinTimeout).ConfigureAwait(false);
            _queue.Dispose();
        }
        catch (TimeoutException)
        {
            // A COM call hangs (Outlook not responding): the thread is a background thread and ends with the process.
        }
    }

    public override string ToString() => nameof(StaWorker);

    private void Loop()
    {
        IDisposable? filter = null;
        try
        {
            if (_useMessageFilter && OperatingSystem.IsWindows())
            {
                try
                {
                    filter = MessageFilterRegistration.TryRegister();
                }
                catch (Exception)
                {
                    // Never a reason for the queue not to run: without the filter a busy Outlook fails sooner (outlook.busy).
                    filter = null;
                }
            }

            foreach (var item in _queue.GetConsumingEnumerable())
            {
                item.Execute();
            }
        }
        finally
        {
            filter?.Dispose();
            _ended.TrySetResult();
        }
    }

    private abstract class WorkItem
    {
        public abstract void Execute();
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private const int Queued = 0;
        private const int Running = 1;
        private const int Done = 2;

        private readonly Func<T> _work;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;
        private int _state;

        public WorkItem(Func<T> work, CancellationToken cancellationToken)
        {
            _work = work;
            _cancellationToken = cancellationToken;
            if (cancellationToken.CanBeCanceled)
            {
                _registration = cancellationToken.Register(static state => ((WorkItem<T>)state!).CancelQueued(), this);
            }
        }

        public Task<T> Task => _completion.Task;

        public override void Execute()
        {
            if (Interlocked.CompareExchange(ref _state, Running, Queued) != Queued)
            {
                return;
            }

            _registration.Dispose();
            try
            {
                _completion.TrySetResult(_work());
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
            finally
            {
                Volatile.Write(ref _state, Done);
            }
        }

        private void CancelQueued()
        {
            if (Interlocked.CompareExchange(ref _state, Done, Queued) == Queued)
            {
                _completion.TrySetCanceled(_cancellationToken);
            }
        }
    }
}

/// <summary>
/// The retry rule of the COM message filter (MailerWPiA <c>ComRetryFilter</c>, design note 4.1): a call Outlook rejects with
/// SERVERCALL_RETRYLATER ("application is busy", e.g. while the user clicks in it) is retried after 150 ms until 60 s have
/// passed since the call was made; then, and for any other rejection, the call is cancelled (it fails with
/// RPC_E_CALL_REJECTED, classified as <c>outlook.busy</c>). Pure, tested on every platform.
/// </summary>
internal static class ComRetryPolicy
{
    /// <summary>SERVERCALL_REJECTED.</summary>
    public const int ServerCallRejected = 1;

    /// <summary>SERVERCALL_RETRYLATER.</summary>
    public const int ServerCallRetryLater = 2;

    public const int RetryDelayMilliseconds = 150;

    public const int GiveUpAfterMilliseconds = 60_000;

    /// <summary>The value of <c>IMessageFilter::RetryRejectedCall</c>: milliseconds to wait before retrying, or -1 (cancel).</summary>
    /// <param name="rejectType">SERVERCALL_REJECTED or SERVERCALL_RETRYLATER.</param>
    /// <param name="elapsedMilliseconds">Milliseconds since the outgoing call was made (<c>dwTickCount</c>).</param>
    public static int RetryDelay(int rejectType, int elapsedMilliseconds) =>
        rejectType == ServerCallRetryLater && elapsedMilliseconds >= 0 && elapsedMilliseconds < GiveUpAfterMilliseconds
            ? RetryDelayMilliseconds
            : -1;
}

/// <summary>IMessageFilter of OLE (ole32), registered per STA thread with <c>CoRegisterMessageFilter</c>.</summary>
[ComImport]
[Guid("00000016-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleMessageFilter
{
    [PreserveSig]
    int HandleInComingCall(int callType, nint threadIdCaller, int tickCount, nint interfaceInfo);

    [PreserveSig]
    int RetryRejectedCall(nint threadIdCallee, int tickCount, int rejectType);

    [PreserveSig]
    int MessagePending(nint threadIdCallee, int tickCount, int pendingType);
}

/// <summary>The COM message filter of the worker thread (<see cref="ComRetryPolicy"/>).</summary>
[SupportedOSPlatform("windows")]
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ComRetryFilter : IOleMessageFilter
{
    private const int ServerCallIsHandled = 0;
    private const int PendingMsgWaitDefProcess = 2;

    public int HandleInComingCall(int callType, nint threadIdCaller, int tickCount, nint interfaceInfo) => ServerCallIsHandled;

    public int RetryRejectedCall(nint threadIdCallee, int tickCount, int rejectType) => ComRetryPolicy.RetryDelay(rejectType, tickCount);

    public int MessagePending(nint threadIdCallee, int tickCount, int pendingType) => PendingMsgWaitDefProcess;
}

/// <summary>Registration of <see cref="ComRetryFilter"/> on the calling STA thread; disposing restores the previous filter.</summary>
[SupportedOSPlatform("windows")]
internal sealed class MessageFilterRegistration : IDisposable
{
    private readonly nint _previous;
    private bool _active = true;

    private MessageFilterRegistration(nint previous)
    {
        _previous = previous;
    }

    /// <summary>Registers the filter; null when COM refuses it (not an STA thread) or it cannot be created – the worker then
    /// runs without retries (MailerWPiA ignored this failure too).</summary>
    public static MessageFilterRegistration? TryRegister()
    {
        nint filter;
        try
        {
            filter = Marshal.GetComInterfaceForObject<ComRetryFilter, IOleMessageFilter>(new ComRetryFilter());
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or COMException or PlatformNotSupportedException)
        {
            return null;
        }

        try
        {
            // COM keeps its own reference to the new filter and hands over its reference to the previous one.
            return ComNative.CoRegisterMessageFilter(filter, out var previous) >= 0 ? new MessageFilterRegistration(previous) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.Release(filter);
        }
    }

    public void Dispose()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        if (ComNative.CoRegisterMessageFilter(_previous, out var ours) >= 0 && ours != 0)
        {
            Marshal.Release(ours);
        }

        if (_previous != 0)
        {
            Marshal.Release(_previous);
        }
    }
}

/// <summary>The native calls of the Outlook engine (Windows only).</summary>
[SupportedOSPlatform("windows")]
internal static partial class ComNative
{
    [LibraryImport("ole32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CLSIDFromProgID(string progId, out Guid clsid);

    /// <summary>oleaut32 GetActiveObject (.NET has no <c>Marshal.GetActiveObject</c>).</summary>
    [LibraryImport("oleaut32.dll")]
    internal static partial int GetActiveObject(in Guid clsid, nint reserved, out nint unknown);

    [LibraryImport("ole32.dll")]
    internal static partial int CoRegisterMessageFilter(nint newFilter, out nint oldFilter);
}
