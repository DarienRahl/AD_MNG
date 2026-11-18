namespace AdMng.Presentation.PowerShell;

/// <summary>
/// What a PowerShell window needs beyond the services of every window (track R, stage R1): the factory of the engine session,
/// the reader of the workstation's policies and what the window's commands call back into. Scoped like
/// <see cref="Dialogs.DialogServices"/>; the host (AdMng.App) registers the implementations. A host without the engine
/// registers none: the window then says so instead of failing.
/// </summary>
/// <param name="sessions">Creates the engine session; null when the build has no engine.</param>
/// <param name="policies">Reads the PowerShell policies; null reads as "none are set".</param>
/// <param name="actions">The grid, the object dialog and the export; null is <see cref="UnavailablePowerShellWindowActions"/>.</param>
public sealed class PowerShellServices(IPowerShellSessionFactory? sessions, IPowerShellPolicyReader? policies, IPowerShellWindowActions? actions)
{
    public IPowerShellSessionFactory? Sessions { get; } = sessions;

    public IPowerShellPolicyReader? Policies { get; } = policies;

    public IPowerShellWindowActions Actions { get; } = actions ?? UnavailablePowerShellWindowActions.Instance;
}
