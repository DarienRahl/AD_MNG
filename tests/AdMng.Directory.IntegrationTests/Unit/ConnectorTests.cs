using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AdMng.Core.Directory;

namespace AdMng.Directory.IntegrationTests.Unit;

/// <summary>Connector behaviour that needs no directory server.</summary>
public sealed class ConnectorTests
{
    [Theory]
    [InlineData(@"u:CONTOSO\jan.kowalski", @"CONTOSO\jan.kowalski", null, "(sAMAccountName=jan.kowalski)")]
    [InlineData("u:jan@contoso.com", "jan@contoso.com", null, "(userPrincipalName=jan@contoso.com)")]
    [InlineData("u:jan", "jan", null, "(sAMAccountName=jan)")]
    [InlineData(@"u:CONTOSO\jan*(x)", @"CONTOSO\jan*(x)", null, @"(sAMAccountName=jan\2a\28x\29)")]
    [InlineData("dn:CN=Jan,DC=contoso,DC=com", "CN=Jan,DC=contoso,DC=com", "CN=Jan,DC=contoso,DC=com", null)]
    [InlineData("anonymous-ish", "anonymous-ish", null, null)]
    public void AuthorizationIds_AreParsed(string authzId, string? identity, string? dn, string? filter)
    {
        var parsed = LdapDirectoryConnector.ParseAuthorizationId(authzId);

        Assert.Equal(identity, parsed.Identity);
        Assert.Equal(dn, parsed.Dn);
        Assert.Equal(filter, parsed.Filter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("u:")]
    [InlineData("dn:")]
    public void EmptyAuthorizationIds_YieldNothing(string? authzId)
    {
        Assert.Equal((null, null, null), LdapDirectoryConnector.ParseAuthorizationId(authzId));
    }

    [Fact]
    public void GetComputerDomain_DoesNotThrow()
    {
        var domain = new LdapDirectoryConnector().GetComputerDomain();

        Assert.True(domain is null || (domain.Length > 0 && !domain.EndsWith('.')));
    }

    [Fact]
    public async Task FindDomainControllers_WithoutLocator_ReturnsTheDomainName()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows uses the DC locator.");

        var dcs = await new LdapDirectoryConnector().FindDomainControllersAsync(" contoso.test. ", TestContext.Current.CancellationToken);

        var dc = Assert.Single(dcs);
        Assert.Equal("contoso.test", dc.HostName);
        Assert.False(dc.IsInClientSite);
    }

    [Fact]
    public async Task FindDomainControllers_RequiresADomain()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new LdapDirectoryConnector().FindDomainControllersAsync(" ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Connect_RefusesClearTextSimpleBind_BeforeAnyNetworkTraffic()
    {
        var options = new ConnectionOptions
        {
            Server = "dc.invalid",
            Authentication = AuthenticationMode.SimpleBind,
            Security = TransportSecurity.None,
            UserName = "jan@contoso.com",
        };

        var error = await Assert.ThrowsAsync<DirectoryException>(() =>
            new LdapDirectoryConnector().ConnectAsync(options, new NetworkCredential(string.Empty, "x"), TestContext.Current.CancellationToken));

        Assert.Equal(DirectoryErrorKind.StrongAuthRequired, error.Kind);
    }

    [Fact]
    public async Task Connect_ToAClosedLocalPort_FailsFastAsServerUnavailable()
    {
        var port = UnusedLocalPort();
        var options = new ConnectionOptions
        {
            Server = "127.0.0.1",
            Port = port,
            Authentication = AuthenticationMode.SimpleBind,
            Security = TransportSecurity.None,
            AllowInsecureSimpleBind = true,
            UserName = "jan@contoso.com",
            ConnectTimeout = TimeSpan.FromSeconds(2),
        };
        var stopwatch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<DirectoryException>(() =>
            new LdapDirectoryConnector().ConnectAsync(options, new NetworkCredential(string.Empty, "x"), TestContext.Current.CancellationToken));

        Assert.Equal(DirectoryErrorKind.ServerUnavailable, error.Kind);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Connect_ToAHostThatAcceptsButNeverAnswers_TimesOut()
    {
        // A listener that accepts TCP connections but never speaks LDAP.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = new ConnectionOptions
        {
            Server = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            Authentication = AuthenticationMode.SimpleBind,
            Security = TransportSecurity.None,
            AllowInsecureSimpleBind = true,
            UserName = "jan@contoso.com",
            RequestTimeout = TimeSpan.FromSeconds(2),
        };
        var stopwatch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<DirectoryException>(() =>
            new LdapDirectoryConnector().ConnectAsync(options, new NetworkCredential(string.Empty, "x"), TestContext.Current.CancellationToken));

        Assert.Contains(error.Kind, new[] { DirectoryErrorKind.Timeout, DirectoryErrorKind.ServerUnavailable });
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Connect_WithoutServerOrJoinedDomain_ExplainsWhatIsMissing()
    {
        var connector = new LdapDirectoryConnector();
        Assert.SkipWhen(connector.GetComputerDomain() is not null, "This computer is joined to a domain.");

        var error = await Assert.ThrowsAsync<DirectoryException>(() =>
            connector.ConnectAsync(new ConnectionOptions(), null, TestContext.Current.CancellationToken));

        Assert.Equal(DirectoryErrorKind.ServerUnavailable, error.Kind);
    }

    private static int UnusedLocalPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
