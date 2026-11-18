using System.Management.Automation.Runspaces;

namespace AdMng.PowerShell.Guards;

/// <summary>
/// The commands the window replaces by a refusal with a clear message (docs/dev/r1-powershell-window.md 4.6). The cmdlet is
/// removed from the initial session state and a function of the same name is registered in its place, so a call by name,
/// through an alias (<c>ipmo</c>, <c>sajb</c>) or with the module name (<c>Microsoft.PowerShell.Core\Import-Module</c>) all
/// meet the refusal. A function has no parameters of its own, so any arguments the operator typed (<c>-ScriptBlock {…}</c>)
/// bind to it and the message is what is shown, as an error of the command's own name (<c>Start-Job: …</c>); like any failed
/// command it does not stop the script unless <c>$ErrorActionPreference</c> says so. A protection against mistakes, like the language mode - not a sandbox.
/// </summary>
public static class CommandGuards
{
    /// <summary>The modules <c>Import-Module</c> may load. Empty in R1; R3 adds the RSAT modules, track C the Microsoft 365 ones.</summary>
    public static IReadOnlyList<string> AllowedModules { get; } = [];

    /// <summary>Commands replaced by a refusal: name and message.</summary>
    public static IReadOnlyList<(string Name, string Message)> Refusals { get; } =
    [
        (
            "Start-Job",
            "Background jobs are not available in the AD Manager PowerShell window: they need a separate pwsh.exe process. Run the command in the window, or start a process explicitly."),
        (
            "Import-Module",
            "Import-Module is limited to a list of modules in the AD Manager PowerShell window, and no module is on that list yet."),
    ];

    /// <summary>Removes the guarded cmdlets from <paramref name="state"/> and adds the refusing functions.</summary>
    internal static void Apply(InitialSessionState state)
    {
        foreach (var (name, message) in Refusals)
        {
            state.Commands.Remove(name, typeof(SessionStateCmdletEntry));
            state.Commands.Add(new SessionStateFunctionEntry(
                name,
                "Write-Error -Message " + QuoteSingle(message) + " -Category PermissionDenied -CategoryActivity " + QuoteSingle(name) + " -ErrorId AdmCommandNotAvailable"));
        }
    }

    private static string QuoteSingle(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
}
