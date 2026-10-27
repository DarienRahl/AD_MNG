using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;

namespace AdMng.Testing;

/// <summary>
/// Records local tool launches (K1): the tool, the host and whether a credential would be handed over – mstsc never gets
/// one; the other tools use the window's credential unless it is the Windows account.
/// </summary>
public sealed class FakeLocalToolLauncher : ILocalToolLauncher
{
    private readonly Lock _gate = new();
    private readonly List<FakeToolLaunch> _launches = [];

    public IReadOnlyList<FakeToolLaunch> Launches
    {
        get
        {
            lock (_gate)
            {
                return [.. _launches];
            }
        }
    }

    /// <summary>The next launch fails with this kind.</summary>
    public RemoteErrorKind? FailNext { get; set; }

    public bool UsesWindowCredential(LocalTool tool, IDirectorySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return tool != LocalTool.RemoteDesktop && session.Info.Authentication != AuthenticationMode.CurrentUser;
    }

    public Task LaunchAsync(LocalTool tool, string host, IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!HostNames.IsValid(host))
        {
            throw new RemoteActionException(RemoteErrorKind.NotSupported);
        }

        if (FailNext is { } kind)
        {
            FailNext = null;
            throw new RemoteActionException(kind);
        }

        lock (_gate)
        {
            _launches.Add(new FakeToolLaunch(tool, host, session.Info.Server, UsesWindowCredential(tool, session)));
        }

        return Task.CompletedTask;
    }
}

/// <summary>One recorded launch.</summary>
public sealed record FakeToolLaunch(LocalTool Tool, string Host, string Server, bool WithWindowCredential);
