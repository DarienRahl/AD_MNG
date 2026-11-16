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

    /// <summary>How long a stopped run may take to end before the window stops waiting for it (a command stuck in .NET code cannot be interrupted).</summary>
    public TimeSpan StopGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Used for the stop grace period; tests inject a fake.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Test seam: commands added to the session state besides the program's.</summary>
    internal IReadOnlyList<SessionStateCommandEntry> ExtraCommands { get; init; } = [];
}
