using System.Management.Automation.Runspaces;

namespace AdMng.PowerShell.Guards;

/// <summary>
/// What the window offers: the ONE list of the session's commands (docs/dev/r1-powershell-window.md 4.6, ADR-0010 decision 2). The
/// session state is an allow-list: <see cref="Apply"/> keeps of the engine's default commands only <see cref="EngineCmdlets"/> and
/// <see cref="EngineFunctions"/>, replaces four cmdlets by commands of the window with fewer parameters (<see cref="Replacements"/>:
/// ForEach-Object without <c>-Parallel</c> and <c>-AsJob</c>, Tee-Object without a file, New-Object without COM, Start-Transcript only
/// to a file the operator names), registers a refusal with a clear message for the names an operator is likely to type
/// (<see cref="Refusals"/>), and keeps the aliases that point at what is left. Everything else - jobs, runspaces, remote sessions,
/// processes, modules, file writers, Invoke-Expression, events, the debugger, help updates, the execution policy - is not in the
/// session at all. The session lists no script file (<see cref="ScriptFileGuard"/> has the other layers) and has no <c>function:</c>
/// drive (a string assigned there would become code past <see cref="InputGuard"/>). AD Manager's own commands are added by the session
/// from <c>AdmCommands</c>.
/// A test (SessionCommandsTests) lists a new session's commands and compares them with this list exactly. In ConstrainedLanguage this
/// keeps a script to the window's commands; in the trusted mode (FullLanguage) .NET code can do anything the process can, the
/// list only means that no command of the window starts a job, a runspace or a module.
/// </summary>
public static class CommandGuards
{
    /// <summary>The engine's own cmdlets the window keeps unchanged: reading objects, shaping them, showing them, files read (never written).</summary>
    public static IReadOnlyList<string> EngineCmdlets { get; } =
    [
        // The pipeline and its objects.
        "Where-Object", "Select-Object", "Sort-Object", "Group-Object", "Measure-Object", "Compare-Object", "Get-Unique", "Join-String",
        "Select-String", "Get-Member", "Add-Member",

        // Formatting and output in the window.
        "Format-Table", "Format-List", "Format-Wide", "Format-Custom", "Format-Hex", "Out-Default", "Out-Host", "Out-String", "Out-Null",

        // Text and data in memory (ConvertTo-SecureString: the guide's history rules name it; a line with a literal is not remembered).
        "ConvertTo-Csv", "ConvertFrom-Csv", "ConvertTo-Json", "ConvertFrom-Json", "ConvertTo-SecureString",

        // Files and locations, read only (a list of names from a file: Get-Content, Import-Csv).
        "Get-Content", "Import-Csv", "Test-Path", "Get-ChildItem", "Get-Item", "Resolve-Path", "Join-Path", "Split-Path", "Get-Location",
        "Set-Location", "Push-Location", "Pop-Location", "Get-PSDrive",

        // Variables and aliases of the session.
        "Get-Variable", "Set-Variable", "New-Variable", "Remove-Variable", "Clear-Variable", "Get-Alias", "Set-Alias", "New-Alias",

        // Finding commands and help.
        "Get-Command", "Get-Help",

        // Values.
        "Get-Date", "New-TimeSpan", "Get-Random", "New-Guid", "Get-Culture", "Get-UICulture",

        // Messages and questions in the window.
        "Write-Host", "Write-Output", "Write-Error", "Write-Warning", "Write-Verbose", "Write-Debug", "Write-Information", "Write-Progress",
        "Read-Host",

        // Scripts typed in the window.
        "Start-Sleep", "Measure-Command", "Set-StrictMode", "Get-ExecutionPolicy",

        // The end of a transcript that Start-Transcript (a replacement) started to a named file.
        "Stop-Transcript",
    ];

    /// <summary>The engine's functions the window keeps: <c>Clear-Host</c> (<c>cls</c>) and <c>TabExpansion2</c>, which Tab completion calls by name.</summary>
    public static IReadOnlyList<string> EngineFunctions { get; } = ["Clear-Host", "TabExpansion2"];

    /// <summary>Engine cmdlets the window replaces by a command of its own with the same name and fewer parameters (<c>ReplacementCommands.cs</c>).</summary>
    public static IReadOnlyList<(string Name, Type Type)> Replacements { get; } =
    [
        ("ForEach-Object", typeof(WindowForEachObjectCommand)),
        ("Tee-Object", typeof(WindowTeeObjectCommand)),
        ("New-Object", typeof(WindowNewObjectCommand)),
        ("Start-Transcript", typeof(WindowStartTranscriptCommand)),
    ];

    /// <summary>The modules <c>Import-Module</c> may load. Empty in R1; R3 adds the RSAT modules, track C the Microsoft 365 ones.</summary>
    public static IReadOnlyList<string> AllowedModules { get; } = [];

    /// <summary>
    /// Names that answer with a message instead of "not recognized": a function of that name writes an error of the command's own name
    /// (<c>Start-Job: …</c>) with the error id <c>AdmCommandNotAvailable</c>. The cmdlets themselves are not in the session.
    /// </summary>
    public static IReadOnlyList<(string Name, string Message)> Refusals { get; } =
    [
        (
            "Start-Job",
            "Background jobs are not available in the AD Manager PowerShell window: a job would keep running after its command ends. Run the command in the window."),
        (
            "Import-Module",
            "Import-Module is limited to a list of modules in the AD Manager PowerShell window, and no module is on that list yet."),
        (
            "Invoke-Command",
            "Invoke-Command is not available in the AD Manager PowerShell window: it has no remote sessions and no background jobs. Run the script block in the window: & { ... }."),
        (
            "Invoke-Expression",
            "Invoke-Expression is not available in the AD Manager PowerShell window: type the command itself."),
        (
            "Get-Credential",
            "Get-Credential is not available in the AD Manager PowerShell window: its commands use the credentials of the connection."),
        (
            "Start-Process",
            "Start-Process is not available in the AD Manager PowerShell window."),
        (
            "Out-GridView",
            "Out-GridView is not available in the AD Manager PowerShell window: use Out-AdmGrid, which shows the objects in the program's grid."),
        ("Export-Csv", FileWriterMessage("Export-Csv")),
        ("Export-Clixml", FileWriterMessage("Export-Clixml")),
        ("Out-File", FileWriterMessage("Out-File")),
        ("Set-Content", FileWriterMessage("Set-Content")),
        ("Add-Content", FileWriterMessage("Add-Content")),
    ];

    /// <summary>Aliases the window adds: <c>help</c> is the engine's paging function elsewhere, here it is Get-Help (no pager runs).</summary>
    public static IReadOnlyList<(string Name, string Definition)> Aliases { get; } = [("help", "Get-Help")];

    /// <summary>The message of the file writers that are not in the window.</summary>
    internal static string FileWriterMessage(string name) =>
        name + " is not available in the AD Manager PowerShell window: it writes a file only through Export-AdmList (a list, formulas protected) or Start-Transcript -Path (a record of the session).";

    /// <summary>
    /// Reduces the engine's default session state (<c>InitialSessionState.CreateDefault()</c>: it brings the formatting and type data,
    /// the providers and the built-in variables) to the window's commands: every command entry not on the lists above is removed.
    /// </summary>
    internal static void Apply(InitialSessionState state)
    {
        var keep = new HashSet<string>([.. EngineCmdlets, .. EngineFunctions], StringComparer.OrdinalIgnoreCase);
        var named = new HashSet<string>([.. keep, .. Replacements.Select(r => r.Name), .. Refusals.Select(r => r.Name), .. Aliases.Select(a => a.Name)], StringComparer.OrdinalIgnoreCase);
        for (var index = state.Commands.Count - 1; index >= 0; index--)
        {
            var entry = state.Commands[index];
            var kept = entry switch
            {
                SessionStateCmdletEntry or SessionStateFunctionEntry => keep.Contains(entry.Name),
                SessionStateAliasEntry alias => named.Contains(alias.Definition) && !named.Contains(alias.Name),

                // Applications ("*") stay: a console program (whoami, nltest) runs as its own process with the operator's identity, never
                // with the window's credentials. Scripts ("*") go: a script file called from the command line is not found.
                SessionStateApplicationEntry => true,
                _ => false,
            };
            if (!kept)
            {
                state.Commands.RemoveItem(index);
            }
        }

        foreach (var (name, type) in Replacements)
        {
            state.Commands.Add(new SessionStateCmdletEntry(name, type, null));
        }

        foreach (var (name, message) in Refusals)
        {
            state.Commands.Add(new SessionStateFunctionEntry(
                name,
                "Write-Error -Message " + QuoteSingle(message) + " -Category PermissionDenied -CategoryActivity " + QuoteSingle(name) + " -ErrorId AdmCommandNotAvailable"));
        }

        foreach (var (name, definition) in Aliases)
        {
            state.Commands.Add(new SessionStateAliasEntry(name, definition));
        }

        // A string assigned to function:x would become code that InputGuard never saw.
        for (var index = state.Providers.Count - 1; index >= 0; index--)
        {
            if (string.Equals(state.Providers[index].Name, "Function", StringComparison.OrdinalIgnoreCase))
            {
                state.Providers.RemoveItem(index);
            }
        }
    }

    private static string QuoteSingle(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
}
