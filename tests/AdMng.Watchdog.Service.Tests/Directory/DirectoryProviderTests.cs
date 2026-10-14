using System.Net;
using AdMng.Core.Directory;
using AdMng.Testing;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Directory;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Fakes;

namespace AdMng.Watchdog.Service.Tests.Directory;

/// <summary>Bootstrap directory settings → connection options, and the pinned session lifecycle.</summary>
public sealed class DirectoryProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Production_UsesTheServiceIdentity()
    {
        var factory = new DirectoryConnectionOptionsFactory(Options(new DirectoryBootstrapOptions { Domain = "contoso.test" }), new FakeHostEnvironment());

        var (options, credential) = factory.Create();

        Assert.Equal("contoso.test", options.Domain);
        Assert.Null(options.Server);
        Assert.Equal(AuthenticationMode.CurrentUser, options.Authentication);
        Assert.Equal(TransportSecurity.SignAndSeal, options.Security);
        Assert.Null(credential);
        Assert.Equal("dc02.contoso.test", factory.Create("dc02.contoso.test").Options.Server);
    }

    [Fact]
    public void LabSimpleBind_ReadsThePasswordOnlyFromTheEnvironment()
    {
        var settings = new DirectoryBootstrapOptions
        {
            Server = "127.0.0.1",
            Authentication = AuthenticationMode.SimpleBind,
            Security = TransportSecurity.None,
            LabSimpleBindUser = "odczyt.test@contoso.test",
            AllowInsecureLab = true,
        };
        var factory = new DirectoryConnectionOptionsFactory(Options(settings), new FakeHostEnvironment("Development"));
        Environment.SetEnvironmentVariable(DirectoryConnectionOptionsFactory.PasswordVariable, null);
        Assert.Throws<InvalidOperationException>(() => factory.Create());

        Environment.SetEnvironmentVariable(DirectoryConnectionOptionsFactory.PasswordVariable, "Hasło-Lab-ż");
        try
        {
            var (options, credential) = factory.Create();

            Assert.True(options.AllowInsecureSimpleBind);
            Assert.Equal("odczyt.test@contoso.test", options.UserName);
            Assert.Equal("Hasło-Lab-ż", credential?.Password);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DirectoryConnectionOptionsFactory.PasswordVariable, null);
        }
    }

    [Fact]
    public void InsecureLab_IsRefusedOutsideDevelopment()
    {
        var factory = new DirectoryConnectionOptionsFactory(
            Options(new DirectoryBootstrapOptions { AllowInsecureLab = true }), new FakeHostEnvironment("Production"));

        Assert.Throws<InvalidOperationException>(() => factory.Create());
    }

    [Fact]
    public async Task Session_IsReused_AndReconnectedAfterAReportedFailure()
    {
        var connector = new CountingConnector();
        var provider = new ServiceDirectorySessionProvider(
            connector,
            new DirectoryConnectionOptionsFactory(Options(new DirectoryBootstrapOptions { Server = "dc01.contoso.test" }), new FakeHostEnvironment()),
            Options(new DirectoryBootstrapOptions { Server = "dc01.contoso.test" }),
            new TestLog().For<ServiceDirectorySessionProvider>());

        var first = await provider.GetSessionAsync(Ct);
        var again = await provider.GetSessionAsync(Ct);
        provider.ReportFailure(first, new DirectoryException(DirectoryErrorKind.ServerUnavailable, "DC01 nie odpowiada"));
        Assert.Equal(DirectoryConnectionState.Failed, provider.State);
        var reconnected = await provider.GetSessionAsync(Ct);

        Assert.Same(first, again);
        Assert.NotSame(first, reconnected);
        Assert.Equal(2, connector.Connects);
        Assert.Equal(DirectoryConnectionState.Connected, provider.State);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task DisabledProvider_NeverConnects()
    {
        var provider = new DisabledDirectorySessionProvider();

        var error = await Assert.ThrowsAsync<DirectoryException>(async () => await provider.GetSessionAsync(Ct));

        Assert.Equal(DirectoryErrorKind.ServerUnavailable, error.Kind);
        Assert.Equal(DirectoryConnectionState.Disconnected, provider.State);
    }

    private static StaticOptionsMonitor<ServiceOptions> Options(DirectoryBootstrapOptions directory) => new(new ServiceOptions { Directory = directory });

    private sealed class CountingConnector : IDirectoryConnector
    {
        public int Connects { get; private set; }

        public string? GetComputerDomain() => "contoso.test";

        public Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DomainControllerInfo>>([]);

        public Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken)
        {
            Connects++;
            return Task.FromResult<IDirectorySession>(new FakeDirectoryBuilder().Build());
        }
    }
}
