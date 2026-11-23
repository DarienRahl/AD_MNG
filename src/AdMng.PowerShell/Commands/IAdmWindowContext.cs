using AdMng.Core.Directory;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// What an AD Manager command may use of the window it runs in (docs/dev/r1-powershell-window.md 4.5). The session keeps it in
/// the engine's window registry, keyed by its runspace, so no variable, no parameter and no member of <c>$Host</c> carries it.
/// <see cref="Services"/> are the window's own services (open an object, the grid, the export); R2 adds the pending plan.
/// </summary>
public interface IAdmWindowContext
{
    /// <summary>
    /// The window's directory session as a reader: searches, base reads, ranged values, <see cref="IDirectoryReader.Info"/>
    /// and the schema. A <see cref="ReadOnlyDirectoryReader"/> - it cannot be cast back to the session, so there is no write
    /// method and no way to the connector's credential (CLAUDE.md rules 2, 4, 5). One DC.
    /// </summary>
    IDirectoryReader Reader { get; }

    /// <summary>The program's version, as shown by <c>Get-AdmVersion</c>.</summary>
    string ApplicationVersion { get; }

    /// <summary>The containers the window's commands returned so far, for the completion of <c>-SearchBase</c> (memory only, bounded).</summary>
    AdmKnownContainers KnownContainers { get; }

    /// <summary>What <c>Out-AdmGrid</c>, <c>Open-AdmObject</c> and <c>Export-AdmList</c> call back into; <see cref="UnavailableWindowServices"/> when the host gave none.</summary>
    IAdmWindowServices Services { get; }
}
