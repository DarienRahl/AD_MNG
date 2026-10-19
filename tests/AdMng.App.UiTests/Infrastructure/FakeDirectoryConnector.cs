using System.Net;
using AdMng.Core.Directory;
using AdMng.Testing;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// Connector returning the prepared in-memory directory (FakeDirectoryBuilder sample company). "Change domain controller"
/// opens a <see cref="FakeDomainControllerSession"/> of the chosen DC over the same directory.
/// </summary>
internal sealed class FakeDirectoryConnector(FakeDirectorySession session) : IDirectoryConnector
{
    public int ConnectCount { get; private set; }

    /// <summary>What the next connect returns instead of the directory itself (e.g. a session of one DC that a switch may close).</summary>
    public IDirectorySession? NextSession { get; set; }

    public string? GetComputerDomain() => FakeDirectoryBuilder.DefaultDomain;

    public Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DomainControllerInfo>>(
        [
            new("dc01.contoso.test", "Warszawa", true, false, true),
            new("dc02.contoso.test", "Kraków", true, true, false),
        ]);

    public Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken)
    {
        ConnectCount++;
        return Task.FromResult(NextSession ?? session);
    }

    public Task<IDirectorySession> ConnectToServerAsync(IDirectorySession current, string server, CancellationToken cancellationToken) =>
        Task.FromResult<IDirectorySession>(new FakeDomainControllerSession(session, server));
}
