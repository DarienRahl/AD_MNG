using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using AdMng.PowerShell.Commands;
using AdMng.PowerShell.Hosting;

namespace AdMng.PowerShell.Completion;

/// <summary>
/// How an argument completer finds the window it runs in: the runspace the completion runs in, its host, the host's private
/// data (docs/dev/r1-powershell-window.md 4.3). A completer reads memory only - the schema of the session and the containers the
/// window already showed - and never the directory.
/// </summary>
internal static class AdmCompletionContext
{
    /// <summary>
    /// The window's services for the completion in progress, or null outside a window. A completer runs on a pipeline thread of the
    /// runspace, which is busy with the completion itself, so the host is fetched by a nested command of the current runspace
    /// (<c>Get-Variable Host</c>) and not through <c>SessionStateProxy</c>.
    /// </summary>
    public static IAdmWindowContext? Current()
    {
        if (Runspace.DefaultRunspace is null)
        {
            return null;
        }

        try
        {
            using var shell = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace);
            var host = shell.AddCommand("Get-Variable").AddParameter("Name", "Host").AddParameter("ValueOnly", true).Invoke();
            return host.Count > 0 && host[0].BaseObject is PSHost { PrivateData.BaseObject: WindowPrivateData data } ? data.Context : null;
        }
        catch (Exception ex) when (ex is InvalidRunspaceStateException or InvalidOperationException or RuntimeException)
        {
            return null;
        }
    }

    /// <summary>The text a completion inserts for <paramref name="value"/>: single-quoted when it needs quoting.</summary>
    public static string Quote(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? value
            : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
