using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Tests.Fakes;

/// <summary>Records exit requests instead of ending the test process.</summary>
public sealed class FakeProcessTerminator : IProcessTerminator
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int? ExitCode { get; private set; }

    public Task<int> Exited => _exited.Task;

    /// <summary>
    /// The exit code once the host's entry point has requested it. A failed start reaches the factory (whose
    /// <c>Services</c> throw) before the entry point's catch block calls <see cref="Exit"/> on its own thread, so a test
    /// that expects an exit waits for it instead of reading <see cref="ExitCode"/> at once.
    /// </summary>
    public async Task<int?> ExitCodeAsync()
    {
        try
        {
            return await Exited.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return ExitCode;
        }
    }

    public void Exit(int exitCode)
    {
        ExitCode = exitCode;
        _exited.TrySetResult(exitCode);
    }
}
