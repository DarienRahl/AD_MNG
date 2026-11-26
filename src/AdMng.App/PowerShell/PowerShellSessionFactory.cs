using AdMng.Core.PowerShell;
using AdMng.PowerShell;
using AdMng.PowerShell.Commands;
using AdMng.Presentation.PowerShell;

namespace AdMng.App.PowerShell;

/// <summary>
/// Creates the engine session of one PowerShell window (track R, stage R1; docs/dev/r1-powershell-window.md 4.1): one runspace,
/// never shared, whose AD Manager commands read through a read-only view of the window's own directory session - never the session
/// itself, so a command has no write method and no way to the connection's credential (CLAUDE.md rules 2, 4 and 5) - and call back
/// into the program through the window's actions. The first runspace of a process takes about half a second, so it is made on a
/// pool thread. Application-wide and stateless: everything of a window is in the request.
/// </summary>
public sealed class PowerShellSessionFactory : IPowerShellSessionFactory
{
    /// <inheritdoc />
    public async Task<IPowerShellSession> CreateAsync(PowerShellSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = new AdmWindowContext(request.Directory, request.ApplicationVersion, new WindowServicesOverActions(request.Actions));
        var options = new PowerShellSessionOptions
        {
            Context = context,
            Culture = request.Culture,
            OutputWidth = request.OutputWidth,
        };
        return await PowerShellSession.CreateAsync(options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The window's commands that call back into the program, over the actions the window was given.</summary>
    private sealed class WindowServicesOverActions(IPowerShellWindowActions actions) : IAdmWindowServices
    {
        public Task ShowGridAsync(PowerShellGridTable table, CancellationToken cancellationToken) => actions.ShowInGridAsync(table, cancellationToken);

        public async Task<bool> OpenObjectAsync(string distinguishedName, CancellationToken cancellationToken) =>
            await actions.OpenObjectAsync(distinguishedName, cancellationToken).ConfigureAwait(false) == PowerShellOpenOutcome.Opened;

        public Task<string?> ExportListAsync(PowerShellGridTable table, string? path, char? delimiter, CancellationToken cancellationToken) =>
            actions.ExportListAsync(table, path, delimiter, cancellationToken);
    }
}
