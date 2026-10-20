namespace AdMng.Watchdog.Keytab;

/// <summary>What the tool talks to: console streams, clock and – for the lab tests only – fault injection.</summary>
public sealed class KeytabToolHost
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    /// <summary>Source of <c>--password-stdin</c>.</summary>
    public required Stream Input { get; init; }

    /// <summary>Stdin is a terminal: the one-time password is read without echo.</summary>
    public bool InputIsTerminal { get; init; }

    /// <summary>Lines get journald priority prefixes (<c>&lt;3&gt;</c> error …) – set when stderr is the journal.</summary>
    public bool Journal { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Wait between two verification rounds while a KDC has not replicated the new key (design 4.4: 30 s).</summary>
    public TimeSpan ReplicationPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Lab tests: called at named steps (<c>changed</c> after a successful password change, <c>written</c>
    /// before the new keytab is committed); an exception thrown there simulates the failure of that step.</summary>
    internal Action<string>? FaultPoint { get; init; }

    /// <summary>The real console of the process.</summary>
    public static KeytabToolHost FromConsole() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        Input = Console.OpenStandardInput(),
        InputIsTerminal = !Console.IsInputRedirected,
        Journal = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JOURNAL_STREAM")),
    };
}
