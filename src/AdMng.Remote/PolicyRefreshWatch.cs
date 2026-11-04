namespace AdMng.Remote;

/// <summary>
/// Watches a computer policy refresh started with <c>gpupdate /wait:0</c> (K1 design note 4.7). gpupdate returns at once
/// while the Group Policy service keeps processing, so its process says nothing about the refresh. Instead the end time of
/// the computer policy processing (Group Policy state, EndTimeHi/EndTimeLo) is read before the launch and polled after it:
/// the refresh has finished when that end time is later than before. Pure apart from the delegates, so it is tested on every
/// platform.
/// </summary>
internal static class PolicyRefreshWatch
{
    /// <summary>Whether <paramref name="current"/> is the end of a processing that finished after <paramref name="before"/>.</summary>
    public static bool IsNewRun(DateTimeOffset? before, DateTimeOffset? current) =>
        current is { } end && (before is not { } previous || end > previous);

    /// <summary>
    /// Reads <paramref name="readEnd"/> every <paramref name="interval"/> (waiting through <paramref name="pause"/>) until it
    /// reports a processing that ended after <paramref name="before"/> or <paramref name="wait"/> has passed.
    /// </summary>
    /// <returns>The end of the new processing; null when it had not finished when the wait ended.</returns>
    public static DateTimeOffset? WaitForNewRun(
        DateTimeOffset? before,
        Func<DateTimeOffset?> readEnd,
        Action<TimeSpan> pause,
        TimeProvider time,
        TimeSpan wait,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readEnd);
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(time);
        var deadline = time.GetUtcNow() + wait;
        while (time.GetUtcNow() < deadline)
        {
            pause(interval);
            cancellationToken.ThrowIfCancellationRequested();
            var end = readEnd();
            if (IsNewRun(before, end))
            {
                return end;
            }
        }

        return null;
    }
}
