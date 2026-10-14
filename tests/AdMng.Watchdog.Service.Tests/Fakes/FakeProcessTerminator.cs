using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Tests.Fakes;

/// <summary>Records exit requests instead of ending the test process.</summary>
public sealed class FakeProcessTerminator : IProcessTerminator
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int? ExitCode { get; private set; }

    public Task<int> Exited => _exited.Task;

    public void Exit(int exitCode)
    {
        ExitCode = exitCode;
        _exited.TrySetResult(exitCode);
    }
}
