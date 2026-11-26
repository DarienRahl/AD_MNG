using System.Management.Automation.Security;
using AdMng.Core.PowerShell;

namespace AdMng.PowerShell;

/// <summary>
/// Asks the system whether it enforces an application control policy (WDAC / AppLocker) that forces ConstrainedLanguage.
/// An ordinary <c>Runspace</c> stays in FullLanguage under such a policy (only the console host and runspace pools enforce
/// it), so the window asks itself (docs/dev/r1-powershell-window.md 4.7). Injected so that tests can report a lockdown.
/// </summary>
public interface IPowerShellLockdownProbe
{
    PowerShellLockdownState Probe();
}

/// <summary>The real probe: <c>SystemPolicy.GetSystemLockdownPolicy()</c> (None everywhere but a Windows machine with a policy).</summary>
public sealed class SystemPolicyLockdownProbe : IPowerShellLockdownProbe
{
    public static SystemPolicyLockdownProbe Instance { get; } = new();

    public PowerShellLockdownState Probe()
    {
        PowerShellEnvironment.Apply();
        return SystemPolicy.GetSystemLockdownPolicy() switch
        {
            SystemEnforcementMode.Enforce => PowerShellLockdownState.Enforce,
            SystemEnforcementMode.Audit => PowerShellLockdownState.Audit,
            _ => PowerShellLockdownState.None,
        };
    }
}
