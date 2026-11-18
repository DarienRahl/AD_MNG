namespace AdMng.Presentation.Feedback;

/// <summary>
/// What an operation changes (track V, stage V2b-5b): whether closing its window while it runs needs a question, and
/// which one. A read changes nothing - its window closes without a question and the read is cancelled. The flag is set
/// by the code that starts the operation (<see cref="PageStatusHolder.Begin"/>, <c>ExecutionStatus</c>), never guessed
/// from a name.
/// </summary>
public enum OperationWrites
{
    /// <summary>A read, a check or a preview: nothing is changed (the default).</summary>
    None,

    /// <summary>The execution of a change plan: Stop finishes the object in progress and starts no other.</summary>
    Changes,

    /// <summary>A send (messages, drafts, a test message): what was accepted already cannot be recalled.</summary>
    Messages,

    /// <summary>Anything else that writes: a restore, an update of the central store, a backup, a save, a remote command.</summary>
    Other,
}

/// <summary>
/// One running operation of a page (track V, stage V2; design note 4.5 and goal G3), started with
/// <see cref="PageStatusHolder.Begin"/>: the page says "working" from the first moment, the operation gets its
/// <see cref="Token"/>, reports how far it is, and ends as done, attention or error - or without a message, when its
/// result is the page itself (a list that was read): the page then shows its summary.
/// </summary>
/// <remarks>
/// <para>Every member may be called from any thread; what changes the page is handed to the UI thread (at once when the
/// caller is on it). An operation started with a <c>using</c> cannot leave the page in "working": disposing a scope
/// that was not completed ends it without a message.</para>
/// <para>The first completion stands; a second one, a report after the end, and everything a replaced operation says
/// are ignored. The token is cancelled by <see cref="Cancel"/>, when the scope is disposed and when the operation is
/// replaced - not by a completion.</para>
/// </remarks>
public sealed class OperationScope : IDisposable
{
    private readonly PageStatusHolder _holder;

    // Never disposed: code that still holds the token may register on it, which throws on a disposed source. A source
    // without a timer or a linked parent holds no unmanaged resource.
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _endedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finishedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _cancelling;
    private volatile bool _ended;
    private volatile bool _finished;

    internal OperationScope(PageStatusHolder holder, PageStatus working, bool canCancel, OperationWrites writes)
    {
        _holder = holder;
        Working = working;
        CanCancel = canCancel;
        Writes = writes;
    }

    /// <summary>Cancelled when the operator stops the operation, when it is replaced and when the scope is disposed.</summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>False for an operation that cannot be stopped once it started: <see cref="Cancel"/> does nothing.</summary>
    public bool CanCancel { get; }

    /// <summary>
    /// What the operation changes. Anything but <see cref="OperationWrites.None"/> makes the window ask before it is closed
    /// while the operation runs (<see cref="RunningWork"/>).
    /// </summary>
    public OperationWrites Writes { get; }

    /// <summary>True for an operation that changes something (<see cref="Writes"/> is not <see cref="OperationWrites.None"/>).</summary>
    public bool IsWrite => Writes != OperationWrites.None;

    /// <summary>
    /// Completes once the operation ended on the page - completed, disposed or replaced - and never fails. The
    /// continuations run asynchronously, so the code that ended the operation has run on to its end first.
    /// </summary>
    public Task Ended => _endedSignal.Task;

    /// <summary>
    /// Completes once the code that runs the operation has ended it - completed or disposed the scope - and never
    /// fails. For an operation that kept the line this is the moment of <see cref="Ended"/>; for one that another
    /// operation took off the line it is later: the page no longer shows it, but its code runs on (a write that cannot
    /// be cancelled is still writing). A window that closes waits for this, not for <see cref="Ended"/>.
    /// </summary>
    public Task Finished => _finishedSignal.Task;

    /// <summary>True once the code that runs the operation has completed or disposed the scope.</summary>
    public bool IsFinished => _finished;

    /// <summary>True from <see cref="Cancel"/> until the operation ended: the page says "Stopping…".</summary>
    public bool IsCancelling => _cancelling && !_ended;

    /// <summary>True once the operation was completed, disposed or replaced.</summary>
    public bool IsEnded => _ended;

    /// <summary>The "working" status as last reported. UI thread (the holder).</summary>
    internal PageStatus Working { get; set; }

    /// <summary>
    /// Says how far the operation is; what is not given stays as it was. Ignored once the operation was asked to stop
    /// or ended.
    /// </summary>
    /// <param name="message">A new text ("Reading “Kraków”: 1 500 objects so far…").</param>
    /// <param name="progress">A new progress.</param>
    /// <param name="log">The value-free copy of the new text (the same key with the new counts).</param>
    public void Report(string? message = null, PageProgress? progress = null, PageStatusLog? log = null)
    {
        if (_ended || _cancelling)
        {
            return;
        }

        _holder.Post(() => _holder.Report(this, message, progress, log));
    }

    /// <summary>
    /// Asks the operation to stop: the page says "Stopping…" at once - the operator gets the answer before the
    /// operation noticed anything - and the token is cancelled. The operation still ends itself (normally as
    /// "attention": what was stopped and what to do). Does nothing for an operation that cannot be cancelled, was asked
    /// before or has ended.
    /// </summary>
    public void Cancel()
    {
        if (!CanCancel || _ended || _cancelling)
        {
            return;
        }

        // First the page, then the token: an operation that answers the cancellation on this thread, inside the call
        // below, completes a scope that already says "Stopping…".
        _holder.Post(() => _holder.MarkCancelling(this));
        _cancellation.Cancel();
    }

    /// <summary>Ends the operation with a result: done, attention or error. A summary or null ends it without a message.</summary>
    public void Complete(PageStatus? result)
    {
        if (_finished)
        {
            return;
        }

        // Also for an operation that was replaced: the page ignores what it says, but its code has ended now.
        _holder.Post(() => _holder.Complete(this, result));
    }

    /// <summary>What happened, how many, and where the result is.</summary>
    public void CompleteDone(string message, PageStatusLog log, PageStatusAction? action = null) =>
        Complete(PageStatus.Done(message, log, action));

    /// <summary>What was skipped (or stopped), why, and what to do.</summary>
    public void CompleteAttention(string message, PageStatusLog log, PageStatusAction? action = null, bool canClose = true) =>
        Complete(PageStatus.Attention(message, log, action, canClose));

    /// <summary>What we tried: why it failed. What to do next (<see cref="PageStatusText.Failure"/>).</summary>
    public void CompleteError(string message, PageStatusLog log, PageStatusAction? action = null, bool canClose = true) =>
        Complete(PageStatus.Error(message, log, action, canClose));

    /// <summary>
    /// Ends the scope: an operation that was not completed ends without a message (the page shows its summary, never
    /// "working"), and the token is cancelled. Safe to call more than once and after a completion.
    /// </summary>
    public void Dispose()
    {
        if (!_finished)
        {
            _holder.Post(() => _holder.Complete(this, null));
        }

        _cancellation.Cancel();
    }

    /// <summary>The operation is marked as asked to stop. False when it was before or has ended. UI thread (the holder).</summary>
    internal bool MarkCancelling()
    {
        if (_ended || _cancelling)
        {
            return false;
        }

        _cancelling = true;
        return true;
    }

    /// <summary>The operation ended on the page. UI thread (the holder).</summary>
    internal void MarkEnded()
    {
        _ended = true;
        _endedSignal.TrySetResult();
    }

    /// <summary>The code that runs the operation ended it. UI thread (the holder).</summary>
    internal void MarkFinished()
    {
        _finished = true;
        _finishedSignal.TrySetResult();
    }

    /// <summary>
    /// The page took this operation off (replaced it or showed something else): it ends on the page and its token is
    /// cancelled. Its code still runs until it completes or disposes the scope (<see cref="Finished"/>).
    /// </summary>
    internal void Abandon()
    {
        _ended = true;
        _cancellation.Cancel();
        _endedSignal.TrySetResult();
    }
}
