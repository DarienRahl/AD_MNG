using System.Management.Automation;
using System.Management.Automation.Host;

namespace AdMng.PowerShell.Guards;

/// <summary>
/// No script file runs in the window (ADR-0010 decision 2): its text would reach the engine past <see cref="InputGuard"/> - a
/// <c>.ps1</c> with <c>using module</c> loads what the window refuses when it is typed. Three layers, because the engine checks a
/// script file in different places: the session lists no script (<see cref="CommandGuards"/>; a script called by name or path from
/// the command line is not found), <see cref="DropScriptFiles"/> drops a script file that a lookup from inside a script block or a
/// function found, and on Windows the engine asks <see cref="WindowAuthorizationManager"/> before it runs any script file or loads a
/// script module, however the script was reached (also a command object from <c>Get-Command</c>). On Linux the engine asks no
/// authorization manager, so there a script obtained as a command object from <c>Get-Command</c> inside a script block still runs;
/// the window is not shipped on Linux.
/// </summary>
internal static class ScriptFileGuard
{
    internal const string Message =
        "Script files (.ps1, .psm1) do not run in the AD Manager PowerShell window: type the commands in the window.";

    /// <summary>The engine's <c>PostCommandLookupAction</c> of the window: a script file found by a lookup is not recognized.</summary>
    internal static void DropScriptFiles(object? sender, CommandLookupEventArgs e)
    {
        if (e.Command is ExternalScriptInfo)
        {
            e.Command = null;
        }
    }

    /// <summary>Whether <paramref name="path"/> is a file the engine would run as script code.</summary>
    internal static bool IsScriptFile(string? path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".psm1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".cdxml", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The window's authorization manager: it refuses every script file (<see cref="ScriptFileGuard"/>) and allows everything else -
/// commands, functions, console programs and the engine's own format and type files. It replaces the engine's execution-policy manager,
/// so the execution policy (still <c>Restricted</c>, <c>Get-ExecutionPolicy</c> shows it) no longer decides about scripts: none runs,
/// whatever a Group Policy sets. The engine calls it on Windows only.
/// </summary>
internal sealed class WindowAuthorizationManager() : AuthorizationManager("Microsoft.PowerShell")
{
    protected override bool ShouldRun(CommandInfo commandInfo, CommandOrigin origin, PSHost host, out Exception? reason)
    {
        if (commandInfo is ExternalScriptInfo script && ScriptFileGuard.IsScriptFile(script.Path))
        {
            reason = new PSSecurityException(ScriptFileGuard.Message);
            return false;
        }

        reason = null;
        return true;
    }
}
