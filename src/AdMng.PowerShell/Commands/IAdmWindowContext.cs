using AdMng.Core.Directory;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// What an AD Manager command may use of the window it runs in (docs/dev/r1-powershell-window.md 4.5). The session injects
/// it through the host, so no global, no variable and no parameter carries it. Part 2 adds the window services (open an
/// object, the grid, the save-path chooser); R2 adds the pending plan.
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
}
