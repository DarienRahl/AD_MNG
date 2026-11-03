namespace AdMng.EventLogs.Tests;

public class NativeCancellationTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public void Register_RunsTheCancelOffTheCancellingThread_SoCancelReturnsAtOnce()
    {
        // E-EVTLOG-04: "Przerwij" cancels on the UI thread; a remote EvtCancel to a DC behind a dropped link blocks until the
        // RPC times out. Here the cancel blocks until released – Cancel() must return before it ends.
        using var release = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        var cancellingThread = Environment.CurrentManagedThreadId;
        var cancelThread = 0;
        object? target = null;
        using var source = new CancellationTokenSource();
        using var registration = NativeCancellation.Register(
            source.Token,
            state =>
            {
                target = state;
                cancelThread = Environment.CurrentManagedThreadId;
                release.Wait(Patience);
                done.Set();
            },
            "dc03.oddział-kraków.contoso.test");

        source.Cancel();

        Assert.False(done.IsSet);
        release.Set();
        Assert.True(done.Wait(Patience, TestContext.Current.CancellationToken));
        Assert.NotEqual(cancellingThread, cancelThread);
        Assert.Equal("dc03.oddział-kraków.contoso.test", target);
    }

    [Fact]
    public void Register_AFailingCancel_NeitherEscapesCancelNorEndsTheProcess()
    {
        using var ran = new ManualResetEventSlim();
        using var source = new CancellationTokenSource();
        using var registration = NativeCancellation.Register(
            source.Token,
            _ =>
            {
                ran.Set();
                throw new UnauthorizedAccessException();
            },
            null);

        source.Cancel();

        Assert.True(ran.Wait(Patience, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Invoke_SwallowsEveryFailure()
    {
        // EventLogException.Throw maps 5, 1223 and 1818 to UnauthorizedAccessException / OperationCanceledException.
        Exception[] failures =
        [
            new UnauthorizedAccessException(),
            new OperationCanceledException(),
            new ObjectDisposedException("EventLogReader"),
            new InvalidOperationException(),
            new IOException("Połączenie RPC zerwane."),
        ];
        var calls = 0;

        foreach (var failure in failures)
        {
            Assert.Null(Record.Exception(() => NativeCancellation.Invoke(_ => { calls++; throw failure; }, null)));
        }

        Assert.Equal(failures.Length, calls);
    }
}
