using System.DirectoryServices.Protocols;
using System.Net;
using AdMng.Core.Directory;
using AdMng.Testing;

namespace AdMng.Directory.IntegrationTests.Unit;

/// <summary>
/// "Change domain controller": the settings of an open session are copied for another DC - same options and credential,
/// a different server - so the user does not type the password again and the copy survives the old session.
/// </summary>
public sealed class ConnectionSettingsForServerTests
{
    private static readonly NetworkCredential Password = new(string.Empty, "Tajne!Hasło1");

    private static ConnectionSettings Credentials() => ConnectionSettings.Create(
        new ConnectionOptions
        {
            Domain = "contoso.test",
            Server = "dc01.contoso.test",
            Port = 3389,
            Authentication = AuthenticationMode.Credentials,
            Security = TransportSecurity.SignAndSeal,
            UserName = @"CONTOSO\łukasz.wiśniewski",
            RequestTimeout = TimeSpan.FromSeconds(42),
            ConnectTimeout = TimeSpan.FromSeconds(7),
            MaxConnections = 3,
        },
        Password,
        isWindows: true);

    [Fact]
    public void ForServer_KeepsEveryOptionButTheServer_AndTheCredential()
    {
        var source = Credentials();

        var copy = source.ForServer(" dc02.contoso.test ");

        Assert.Equal(source.Options with { Server = "dc02.contoso.test" }, copy.Options);
        Assert.Equal(AuthType.Negotiate, copy.AuthType);
        Assert.Equal(3389, copy.Port);
        Assert.Equal(source.Account!.ToString(), copy.Account!.ToString());
        Assert.NotSame(source.Credential, copy.Credential);
        Assert.Equal("łukasz.wiśniewski", copy.Credential!.UserName);
        Assert.Equal("CONTOSO", copy.Credential.Domain);
        Assert.Equal("Tajne!Hasło1", copy.Credential.Password);
    }

    [Fact]
    public void ForServer_Copy_OutlivesTheSourceSession()
    {
        var source = Credentials();
        var copy = source.ForServer("dc02.contoso.test");

        source.ClearCredential();

        Assert.Null(source.Credential);
        Assert.Equal("Tajne!Hasło1", copy.Credential!.Password);
    }

    [Fact]
    public void ForServer_OfAClosedSession_IsRefused()
    {
        var source = Credentials();
        source.ClearCredential();

        Assert.Throws<ObjectDisposedException>(() => source.ForServer("dc02.contoso.test"));
    }

    [Fact]
    public void ForServer_WithTheProcessIdentity_HasNoCredential()
    {
        var source = ConnectionSettings.Create(new ConnectionOptions { Domain = "contoso.test" }, null, isWindows: true);
        source.ClearCredential();

        var copy = source.ForServer("dc02.contoso.test");

        Assert.Null(copy.Credential);
        Assert.Equal("dc02.contoso.test", copy.Options.Server);
        Assert.Equal(AuthenticationMode.CurrentUser, copy.Options.Authentication);
    }

    [Fact]
    public void ForServer_SimpleBindOverTls_KeepsTheBindName()
    {
        var source = ConnectionSettings.Create(
            new ConnectionOptions
            {
                Server = "127.0.0.1",
                Authentication = AuthenticationMode.SimpleBind,
                Security = TransportSecurity.Tls,
                UserName = "jan.kowalski@contoso.test",
                AllowUntrustedCertificate = true,
            },
            Password,
            isWindows: false);

        var copy = source.ForServer("dc02.contoso.test");

        Assert.Equal(AuthType.Basic, copy.AuthType);
        Assert.Equal("jan.kowalski@contoso.test", copy.Credential!.UserName);
        Assert.True(copy.UsesTransportEncryption);
        Assert.True(copy.Options.AllowUntrustedCertificate);
        Assert.Equal(636, copy.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dc02.contoso.test:636")]
    public void ForServer_RejectsInvalidNames(string server)
    {
        Assert.Throws<ArgumentException>(() => Credentials().ForServer(server));
    }

    [Fact]
    public void ForServer_NeverShowsThePassword()
    {
        var copy = Credentials().ForServer("dc02.contoso.test");

        Assert.DoesNotContain("Tajne", copy.ToString(), StringComparison.Ordinal);
        Assert.Contains("dc02.contoso.test", copy.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectToServer_RefusesSessionsOfOtherConnectors()
    {
        await using var fake = new FakeDirectorySession();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new LdapDirectoryConnector().ConnectToServerAsync(fake, "dc02.contoso.test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectToServer_IsNotSupportedByConnectorsWithoutAnImplementation()
    {
        await using var fake = new FakeDirectorySession();
        IDirectoryConnector connector = new MinimalConnector();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            connector.ConnectToServerAsync(fake, "dc02.contoso.test", TestContext.Current.CancellationToken));
    }

    private sealed class MinimalConnector : IDirectoryConnector
    {
        public string? GetComputerDomain() => null;

        public Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DomainControllerInfo>>([]);

        public Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
