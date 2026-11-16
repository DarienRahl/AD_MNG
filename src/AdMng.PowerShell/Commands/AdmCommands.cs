using System.Management.Automation.Runspaces;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// The one catalog of AD Manager's commands (docs/dev/r1-powershell-window.md 4.5, 4.8): name and class. The initial session
/// state registers exactly these; a test compares the list with the expected one, with the assembly's <c>[Cmdlet]</c> types
/// and with a live session, and fails when a write verb is among them (D9: R1 has no write command - R2 changes the list and
/// that test together).
/// </summary>
public static class AdmCommands
{
    /// <summary>Command name and its class, in the order they are registered.</summary>
    public static IReadOnlyList<(string Name, Type Type)> Catalog { get; } =
    [
        ("Get-AdmVersion", typeof(GetAdmVersionCommand)),
        ("Get-AdmObject", typeof(GetAdmObjectCommand)),
        ("Get-AdmUser", typeof(GetAdmUserCommand)),
        ("Get-AdmGroup", typeof(GetAdmGroupCommand)),
        ("Get-AdmComputer", typeof(GetAdmComputerCommand)),
        ("Get-AdmGroupMember", typeof(GetAdmGroupMemberCommand)),
        ("Search-AdmObject", typeof(SearchAdmObjectCommand)),
    ];

    /// <summary>The MAML help file of the commands (English), beside the assembly.</summary>
    public const string HelpFileName = "AdMng.PowerShell.dll-Help.xml";

    /// <summary>The names of the registered commands.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Catalog.Select(entry => entry.Name)];

    /// <summary>The session state entries of the catalog.</summary>
    internal static IEnumerable<SessionStateCommandEntry> Entries()
    {
        var help = HelpFilePath();
        return Catalog.Select(entry => new SessionStateCmdletEntry(entry.Name, entry.Type, help));
    }

    /// <summary>
    /// The help file beside the program (<see cref="AppContext.BaseDirectory"/>: the folder of the test or of <c>AdMng.exe</c>, where the
    /// build and the publish put it), or null when there is none. Not <c>Assembly.Location</c>: it is empty in a single-file app.
    /// </summary>
    private static string? HelpFilePath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, HelpFileName);
        return File.Exists(path) ? path : null;
    }
}
