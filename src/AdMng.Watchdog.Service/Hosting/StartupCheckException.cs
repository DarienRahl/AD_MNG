namespace AdMng.Watchdog.Service.Hosting;

/// <summary>A start-up check failed; the process exits with <see cref="ExitCode"/>. The message is a constant operator
/// hint (no directory values, no secrets).</summary>
public sealed class StartupCheckException : Exception
{
    public StartupCheckException(int exitCode, string message)
        : base(message)
    {
        ExitCode = exitCode;
    }

    public StartupCheckException()
        : this(StartupExitCodes.Fatal, "A start-up check failed.")
    {
    }

    public StartupCheckException(string message)
        : this(StartupExitCodes.Fatal, message)
    {
    }

    public StartupCheckException(string message, Exception innerException)
        : base(message, innerException)
    {
        ExitCode = StartupExitCodes.Fatal;
    }

    public int ExitCode { get; }
}
