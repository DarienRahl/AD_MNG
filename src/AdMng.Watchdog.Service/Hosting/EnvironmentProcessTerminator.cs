namespace AdMng.Watchdog.Service.Hosting;

/// <summary><see cref="IProcessTerminator"/> over <see cref="Environment.Exit"/>.</summary>
public sealed class EnvironmentProcessTerminator : IProcessTerminator
{
    public void Exit(int exitCode) => Environment.Exit(exitCode);
}
