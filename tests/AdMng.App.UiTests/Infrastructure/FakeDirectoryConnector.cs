using System.Net;
using AdMng.Core.Directory;
using AdMng.Testing;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// Connector returning the prepared in-memory directory (FakeDirectoryBuilder sample company). "Change domain controller"
/// opens a <see cref="FakeDomainControllerSession"/> of the chosen DC over the same directory. Further domains
/// (<see cref="AddDomain"/>, e.g. fabrikam.test of a second connection window) are connected with NON-owning sessions:
/// closing that window never disposes the fake another window or the next connect uses.
/// </summary>
internal sealed class FakeDirectoryConnector(FakeDirectorySession session) : IDirectoryConnector
{
    private readonly Dictionary<string, FakeDirectorySession> _domains = new(StringComparer.OrdinalIgnoreCase);

    public int ConnectCount { get; private set; }

    /// <summary>What the next connect returns instead of the directory itself (e.g. a session of one DC that a switch may close).</summary>
    public IDirectorySession? NextSession { get; set; }

    /// <summary>Every connect returns a non-owning session, also for the default domain (several windows on one fake).</summary>
    public bool NonOwning { get; set; }

    /// <summary>When set, binds wait for it (a slow DC) and ignore cancellation, like a native bind.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Another domain the connect window can reach (by its DNS name).</summary>
    public void AddDomain(FakeDirectorySession directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _domains[directory.Info.DomainDnsName] = directory;
    }

    public string? GetComputerDomain() => FakeDirectoryBuilder.DefaultDomain;

    public Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DomainControllerInfo>>(
        [
            new($"dc01.{domain}", "Warszawa", true, false, true),
            new($"dc02.{domain}", "Kraków", true, true, false),
        ]);

    public async Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ConnectCount++;
        if (Gate is { } gate)
        {
            await gate.Task;
        }

        if (NextSession is { } next)
        {
            return next;
        }

        var user = credential?.UserName ?? options.UserName;
        if (options.Domain is { } name && _domains.TryGetValue(name, out var other))
        {
            return Wrap(other, user);
        }

        return NonOwning ? Wrap(session, user) : session;
    }

    public Task<IDirectorySession> ConnectToServerAsync(IDirectorySession current, string server, CancellationToken cancellationToken)
    {
        var directory = current is FakeDomainControllerSession wrapper ? wrapper.Directory : session;
        var identity = current.Info.BoundIdentity;
        return Task.FromResult<IDirectorySession>(new FakeDomainControllerSession(directory, server, info => info with { BoundIdentity = identity }));
    }

    private static FakeDomainControllerSession Wrap(FakeDirectorySession directory, string? user) =>
        new(directory, directory.Info.Server, info => user is null ? info : info with { BoundIdentity = $@"{info.DomainNetBiosName}\{user}" });
}
