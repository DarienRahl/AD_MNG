using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.PowerShell;

namespace AdMng.Presentation.PowerShell;

/// <summary>
/// Creates the engine session of one PowerShell window (track R, docs/dev/r1-powershell-window.md 3 and 4.1). The contract is
/// here and the implementation is in AdMng.App, the only project that references the engine - the pattern of the remote
/// actions: the view model never sees an engine type, so it is tested against a fake <see cref="IPowerShellSession"/>.
/// </summary>
public interface IPowerShellSessionFactory
{
    /// <summary>
    /// Opens a runspace for the window: one per window, never shared, disposed with the window. The first one of a process takes
    /// about half a second, so it is created off the UI thread. The window disposes the session it gets.
    /// </summary>
    Task<IPowerShellSession> CreateAsync(PowerShellSessionRequest request, CancellationToken cancellationToken);
}

/// <summary>What a window gives the factory: the session its commands read from and the services they call back into.</summary>
/// <param name="Directory">The window's directory session (one DC): the commands get a read-only view of it, never the session itself.</param>
/// <param name="ApplicationVersion">The program's version, shown by <c>Get-AdmVersion</c>.</param>
/// <param name="Culture">The UI language of the window: the culture of the engine.</param>
/// <param name="OutputWidth">The width, in characters, of the engine's formatting at the start.</param>
/// <param name="Actions">What <c>Out-AdmGrid</c>, <c>Open-AdmObject</c> and <c>Export-AdmList</c> call back into.</param>
public sealed record PowerShellSessionRequest(
    IDirectorySession Directory,
    string ApplicationVersion,
    CultureInfo Culture,
    int OutputWidth,
    IPowerShellWindowActions Actions)
{
    /// <summary>Names nothing: the session may hold a credential and the request is never logged.</summary>
    public override string ToString() => nameof(PowerShellSessionRequest);
}
