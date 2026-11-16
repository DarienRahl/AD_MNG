using AdMng.Core.Directory;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// The window's context as the program builds it: the connection's session behind a <see cref="ReadOnlyDirectoryReader"/>,
/// so a command can search and read but can never be handed (or cast to) the session with its write methods and its
/// credential (CLAUDE.md rules 2, 4 and 5; ADR-0010 decision 9: R1 has no write). R2 adds the pending plan beside it.
/// </summary>
public sealed class AdmWindowContext : IAdmWindowContext
{
    public AdmWindowContext(IDirectoryReader reader, string applicationVersion)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(applicationVersion);
        Reader = ReadOnlyDirectoryReader.Of(reader);
        ApplicationVersion = applicationVersion;
    }

    /// <inheritdoc />
    public IDirectoryReader Reader { get; }

    /// <inheritdoc />
    public string ApplicationVersion { get; }
}
