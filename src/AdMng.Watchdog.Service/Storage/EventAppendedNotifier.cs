namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// In-process wake-up for <c>IEventStore.WaitForNewerAsync</c> (long-poll, spec 17.2): the store is the only writer of the
/// database, so every insert of events completes the current task and arms a new one.
/// </summary>
internal sealed class EventAppendedNotifier
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _appended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A task that completes at the next <see cref="Notify"/>.</summary>
    public Task Next
    {
        get
        {
            lock (_gate)
            {
                return _appended.Task;
            }
        }
    }

    /// <summary>Called after a transaction that inserted events committed.</summary>
    public void Notify()
    {
        TaskCompletionSource appended;
        lock (_gate)
        {
            appended = _appended;
            _appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        appended.TrySetResult();
    }
}
