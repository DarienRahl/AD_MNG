using System.Net;
using AdMng.Core.Directory;
using AdMng.Testing;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>Connector returning the prepared in-memory directory (FakeDirectoryBuilder sample company).</summary>
internal sealed class FakeDirectoryConnector(FakeDirectorySession session) : IDirectoryConnector
{
    public int ConnectCount { get; private set; }

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
        return Task.FromResult<IDirectorySession>(session);
    }
}
