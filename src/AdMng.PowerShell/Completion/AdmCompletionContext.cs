using AdMng.PowerShell.Commands;
using AdMng.PowerShell.Hosting;

namespace AdMng.PowerShell.Completion;

/// <summary>
/// How an argument completer finds the window it runs in: the window registry of the runspace the completion runs in (a completer runs
/// on that runspace's pipeline thread; docs/dev/r1-powershell-window.md 4.3). A completer reads memory only - the schema of the session
/// and the containers the window already showed - and never the directory.
/// </summary>
internal static class AdmCompletionContext
{
    /// <summary>The window's services for the completion in progress, or null outside a window.</summary>
    public static IAdmWindowContext? Current() => WindowRegistry.Current()?.Context;

    /// <summary>The text a completion inserts for <paramref name="value"/>: single-quoted when it needs quoting.</summary>
    public static string Quote(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? value
            : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
