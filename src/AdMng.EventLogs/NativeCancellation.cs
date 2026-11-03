namespace AdMng.EventLogs;

/// <summary>
/// The native cancels of a read (<c>EvtCancel</c> through <c>CancelCurrentOperations</c> / <c>CancelReading</c>),
/// registered on its token (rule 6). A token is cancelled synchronously by whoever cancels it – "Przerwij", closing the
/// document or the connection, on the UI thread, through the linked sources of the readers – and <c>EvtCancel</c> on a
/// remote session or reader is a call to the target (MS-EVEN6), which on a dropped link waits for the connection timeout.
/// The registration therefore only queues the cancel to the thread pool. The cancel never throws: a callback failure
/// would escape from the caller's <c>Cancel()</c>, a work item failure would end the process.
/// </summary>
internal static class NativeCancellation
{
    /// <summary>Runs <paramref name="cancel"/> with <paramref name="state"/> on the thread pool once the token is cancelled.</summary>
    public static CancellationTokenRegistration Register(CancellationToken cancellationToken, Action<object?> cancel, object? state)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        return cancellationToken.Register(
            static pending => ThreadPool.UnsafeQueueUserWorkItem(static p => p.Run(), (Pending)pending!, preferLocal: false),
            new Pending(cancel, state));
    }

    /// <summary>Runs <paramref name="cancel"/>; any failure is swallowed (the read ends anyway, the handle may be gone).</summary>
    public static void Invoke(Action<object?> cancel, object? state)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        try
        {
            cancel(state);
        }
        catch (Exception)
        {
            // Already finished, disposed, or refused by the target (access, cancelled RPC): nothing to stop any more.
        }
    }

    private sealed class Pending(Action<object?> cancel, object? state)
    {
        public void Run() => Invoke(cancel, state);
    }
}
