namespace AdMng.Presentation.Infrastructure;

/// <summary>
/// "Latest request wins": every <see cref="Next"/> cancels the token handed out before. Used to cancel stale
/// list loads, search-as-you-type queries and detail reads when the operator moves on.
/// </summary>
/// <remarks>
/// Previous sources are cancelled but intentionally not disposed: code that still holds their token may call
/// <see cref="CancellationToken.Register(Action)"/>, which throws on a disposed source. Sources without timers
/// or linked parents hold no unmanaged resources.
/// </remarks>
public sealed class SerialCancellation
{
    private CancellationTokenSource? _current;

    /// <summary>Cancels the previous token and returns a fresh one.</summary>
    public CancellationToken Next()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _current, next);
        previous?.Cancel();
        return next.Token;
    }

    /// <summary>Cancels the current token (if any).</summary>
    public void Cancel() => Interlocked.Exchange(ref _current, null)?.Cancel();

    /// <summary>True when <paramref name="token"/> is the most recent one and not cancelled.</summary>
    public bool IsCurrent(CancellationToken token)
    {
        var current = Volatile.Read(ref _current);
        return current is not null && current.Token == token && !token.IsCancellationRequested;
    }
}

/// <summary>
/// Debounces a stream of requests (search as you type): only the last request within the delay runs, and
/// starting a new request cancels the running one. Time is injectable for tests.
/// </summary>
public sealed class Debouncer(TimeProvider timeProvider, TimeSpan delay)
{
    private readonly SerialCancellation _serial = new();

    public TimeSpan Delay { get; set; } = delay;

    /// <summary>
    /// Waits for the debounce delay and then runs <paramref name="action"/> unless a newer request arrived.
    /// Completes normally (without throwing) when superseded.
    /// </summary>
    public async Task RunAsync(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var token = _serial.Next();
        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, timeProvider, token).ConfigureAwait(true);
            }

            token.ThrowIfCancellationRequested();
            await action(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by a newer request.
        }
    }

    /// <summary>Cancels the pending/running request.</summary>
    public void Cancel() => _serial.Cancel();
}
