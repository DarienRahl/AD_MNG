using System.DirectoryServices.Protocols;
using System.Net;
using AdMng.Core.Directory;
using DirectoryException = AdMng.Core.Directory.DirectoryException;

namespace AdMng.Directory.IntegrationTests.Unit;

public sealed class ConnectionSettingsTests
{
    private static readonly NetworkCredential Password = new(string.Empty, "Tajne!Hasło1");

    [Fact]
    public void CurrentUser_UsesNegotiateWithoutCredential()
    {
        var settings = ConnectionSettings.Create(new ConnectionOptions(), Password, isWindows: true);

        Assert.Equal(AuthType.Negotiate, settings.AuthType);
        Assert.Null(settings.Credential);
        Assert.Null(settings.Account);
        Assert.Equal(389, settings.Port);
        Assert.False(settings.UsesTransportEncryption);
    }

    [Fact]
    public void Credentials_OnWindows_BuildANegotiateCredential()
    {
        var options = new ConnectionOptions
        {
            Authentication = AuthenticationMode.Credentials,
            UserName = @"CONTOSO\helpdesk",
        };

        var settings = ConnectionSettings.Create(options, Password, isWindows: true);

        Assert.Equal(AuthType.Negotiate, settings.AuthType);
        Assert.Equal("helpdesk", settings.Credential!.UserName);
        Assert.Equal("CONTOSO", settings.Credential.Domain);
        Assert.Equal("Tajne!Hasło1", settings.Credential.Password);
        Assert.Equal(@"CONTOSO\helpdesk", settings.Account!.ToString());
    }

    [Fact]
    public void Credentials_TakeTheUserNameFromTheCredentialWhenOptionsHaveNone()
    {
        var options = new ConnectionOptions { Authentication = AuthenticationMode.Credentials, Domain = "contoso.com" };

        var settings = ConnectionSettings.Create(options, new NetworkCredential("jan", "x", "CONTOSO"), isWindows: true);

        Assert.Equal("jan", settings.Credential!.UserName);
        Assert.Equal("CONTOSO", settings.Credential.Domain);
    }

    [Fact]
    public void Credentials_WithoutUserName_AreRejected()
    {
        var options = new ConnectionOptions { Authentication = AuthenticationMode.Credentials };

        Assert.Throws<ArgumentException>(() => ConnectionSettings.Create(options, Password, isWindows: true));
    }

    [Fact]
    public void Credentials_WithoutPassword_AreRejected()
    {
        var options = new ConnectionOptions { Authentication = AuthenticationMode.Credentials, UserName = "jan@contoso.com" };

        Assert.Throws<ArgumentException>(() => ConnectionSettings.Create(options, null, isWindows: true));
        Assert.Throws<ArgumentException>(() => ConnectionSettings.Create(options, new NetworkCredential("jan", string.Empty), isWindows: true));
    }

    [Fact]
    public void Credentials_WithADistinguishedName_AreRejected()
    {
        var options = new ConnectionOptions { Authentication = AuthenticationMode.Credentials, UserName = "CN=Jan,DC=contoso,DC=com" };

        Assert.Throws<ArgumentException>(() => ConnectionSettings.Create(options, Password, isWindows: true));
    }

    [Fact]
    public void Credentials_OffWindows_AreNotSupported()
    {
        var options = new ConnectionOptions { Authentication = AuthenticationMode.Credentials, UserName = "jan@contoso.com" };

        Assert.Throws<PlatformNotSupportedException>(() => ConnectionSettings.Create(options, Password, isWindows: false));
    }

    [Fact]
    public void SimpleBind_OverAnUnprotectedChannel_IsRefusedWithoutOptIn()
    {
        var options = new ConnectionOptions
        {
            Authentication = AuthenticationMode.SimpleBind,
            Security = TransportSecurity.None,
            UserName = "jan@contoso.com",
        };

        var error = Assert.Throws<DirectoryException>(() => ConnectionSettings.Create(options, Password, isWindows: false));
        Assert.Equal(DirectoryErrorKind.StrongAuthRequired, error.Kind);

        var lab = ConnectionSettings.Create(options with { AllowInsecureSimpleBind = true }, Password, isWindows: false);
        Assert.Equal(AuthType.Basic, lab.AuthType);
        Assert.Equal("jan@contoso.com", lab.Credential!.UserName);
    }

    [Fact]
    public void SimpleBind_WithSignAndSeal_IsAnInvalidCombination()
    {
        var options = new ConnectionOptions { Authentication = AuthenticationMode.SimpleBind, UserName = "jan@contoso.com" };

        Assert.Throws<ArgumentException>(() => ConnectionSettings.Create(options, Password, isWindows: true));
    }

    [Theory]
    [InlineData(TransportSecurity.Tls, 636)]
    [InlineData(TransportSecurity.StartTls, 389)]
    public void SimpleBind_OverTls_UsesTheImplicitUpnAndTheRightPort(TransportSecurity security, int port)
    {
        var options = new ConnectionOptions
        {
            Authentication = AuthenticationMode.SimpleBind,
            Security = security,
            Domain = "contoso.test",
            UserName = "administrator",
        };

        var settings = ConnectionSettings.Create(options, Password, isWindows: false);

        Assert.Equal(AuthType.Basic, settings.AuthType);
        Assert.Equal("administrator@contoso.test", settings.Credential!.UserName);
        Assert.Equal(port, settings.Port);
        Assert.True(settings.UsesTransportEncryption);
    }

    [Fact]
    public void SimpleBind_WithPlainNameAndSeparateDomain_UsesTheDownLevelForm()
    {
        var options = new ConnectionOptions
        {
            Authentication = AuthenticationMode.SimpleBind,
            Security = TransportSecurity.Tls,
            UserName = "jan",
        };

        var settings = ConnectionSettings.Create(options, new NetworkCredential("ignored", "x", "CONTOSO"), isWindows: true);

        Assert.Equal(@"CONTOSO\jan", settings.Credential!.UserName);
    }

    [Fact]
    public void GlobalCatalogPorts_FollowTheSecurity()
    {
        Assert.Equal(3268, ConnectionSettings.Create(new ConnectionOptions { GlobalCatalog = true }, null, true).Port);
        var tls = new ConnectionOptions { GlobalCatalog = true, Security = TransportSecurity.Tls };
        Assert.Equal(3269, ConnectionSettings.Create(tls, null, true).Port);
        Assert.Equal(1636, ConnectionSettings.Create(tls with { Port = 1636 }, null, true).Port);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void InvalidPort_IsRejected(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ConnectionSettings.Create(new ConnectionOptions { Port = port }, null, true));
    }

    [Fact]
    public void InvalidLimits_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ConnectionSettings.Create(new ConnectionOptions { MaxConnections = 0 }, null, true));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ConnectionSettings.Create(new ConnectionOptions { RequestTimeout = TimeSpan.Zero }, null, true));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ConnectionSettings.Create(new ConnectionOptions { ConnectTimeout = TimeSpan.FromSeconds(-1) }, null, true));
        Assert.Throws<ArgumentException>(() => ConnectionSettings.Create(new ConnectionOptions { Server = " " }, null, true));
        Assert.Throws<ArgumentException>(() => ConnectionSettings.Create(new ConnectionOptions { Server = "dc01:636" }, null, true));
        Assert.Equal(389, ConnectionSettings.Create(new ConnectionOptions { Server = "fe80::1" }, null, true).Port);
        Assert.Throws<ArgumentException>(() => ConnectionSettings.Create(new ConnectionOptions { Domain = "" }, null, true));
        Assert.Throws<ArgumentNullException>(() => ConnectionSettings.Create(null!, null, true));
    }

    [Fact]
    public void ToString_NeverContainsThePassword_AndClearDropsTheCredential()
    {
        var options = new ConnectionOptions
        {
            Server = "dc01.contoso.com",
            Authentication = AuthenticationMode.SimpleBind,
            Security = TransportSecurity.Tls,
            UserName = "jan@contoso.com",
        };
        var settings = ConnectionSettings.Create(options, Password, isWindows: true);

        Assert.DoesNotContain("Tajne", settings.ToString(), StringComparison.Ordinal);
        Assert.Contains("dc01.contoso.com:636", settings.ToString(), StringComparison.Ordinal);

        settings.ClearCredential();
        Assert.Null(settings.Credential);
    }
}
