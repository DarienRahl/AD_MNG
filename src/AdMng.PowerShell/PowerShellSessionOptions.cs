using System.Globalization;
using System.Management.Automation.Runspaces;
using AdMng.PowerShell.Commands;

namespace AdMng.PowerShell;

/// <summary>What a window gives its session when it creates it.</summary>
public sealed class PowerShellSessionOptions
{
    /// <summary>The window's services for AD Manager's commands; null for a session without a window (tests of the engine itself).</summary>
    public IAdmWindowContext? Context { get; init; }

    /// <summary>The lockdown probe; the real one (<see cref="SystemPolicyLockdownProbe"/>) unless a test injects another.</summary>
    public IPowerShellLockdownProbe LockdownProbe { get; init; } = SystemPolicyLockdownProbe.Instance;

    /// <summary>Columns the engine's formatting uses; the window changes it with <see cref="PowerShellSession.OutputWidth"/> when it is resized.</summary>
    public int OutputWidth { get; init; } = 120;

    /// <summary>The culture of the engine (the window's UI language); the current culture when null.</summary>
    public CultureInfo? Culture { get; init; }

    /// <summary>How long a stopped run may take to end before the session leaves it and goes on in a new runspace (a command stuck in .NET code cannot be interrupted).</summary>
    public TimeSpan StopGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a completion (Tab) runs before the session stops it (5 s).</summary>
    public static readonly TimeSpan DefaultCompletionTimeLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a completion may run before the session stops it (audit PS-10: completion runs <c>TabExpansion2</c> and argument
    /// completers, which the operator can replace with code that never returns); a completion that its stop cannot end either is left
    /// after <see cref="StopGracePeriod"/>, like a run.
    /// </summary>
    public TimeSpan CompletionTimeLimit { get; init; } = DefaultCompletionTimeLimit;

    /// <summary>Used for the stop grace period and the time limit of a completion; tests inject a fake.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Test seam: commands added to the session state besides the program's.</summary>
    internal IReadOnlyList<SessionStateCommandEntry> ExtraCommands { get; init; } = [];

    /// <summary>Test seam: called before each runspace of the session is made, with its number (1 is the first); what it throws is that runspace's failure.</summary>
    internal Action<int>? RunspaceCreating { get; init; }
}
