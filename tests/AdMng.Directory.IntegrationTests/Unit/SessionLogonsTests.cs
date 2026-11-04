using System.Net;
using AdMng.Core.Directory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Directory.IntegrationTests.Unit;

/// <summary>The Kerberos logon of a credential window for the remote actions (K1 design note 4.7).</summary>
public sealed class SessionLogonsTests
{
    [Theory]
    [InlineData("łukasz.wójcik", null, "łukasz.wójcik", "contoso.test")]
    [InlineData("łukasz.wójcik", "corp.contoso.test", "łukasz.wójcik", "corp.contoso.test")]
    [InlineData(@"CONTOSO\łukasz.wójcik", null, "łukasz.wójcik", "CONTOSO")]
    [InlineData("łukasz.wójcik@contoso.test", null, "łukasz.wójcik@contoso.test", null)]
    public async Task Of_APlainNameWithoutATypedDomain_TakesTheDomainOfTheSession(string userName, string? typedDomain, string user, string? domain)
    {
        // Not joined (or the domain field left empty): the Negotiate bind worked, Kerberos needs the realm.
        var options = new ConnectionOptions
        {
            Server = "dc01.contoso.test",
            Authentication = AuthenticationMode.Credentials,
            UserName = userName,
            Domain = typedDomain,
        };
        var settings = ConnectionSettings.Create(options, new NetworkCredential(string.Empty, "Zażółć!2026"), isWindows: true);
        var factory = new LdapConnectionFactory(settings, "dc01.contoso.test", "contoso.test", NullLogger.Instance);
        await using var pool = new LdapConnectionPool(factory, 1, NullLogger.Instance, TimeProvider.System);
        var info = new SessionInfo
        {
            Server = "dc01.contoso.test",
            Port = 389,
            DomainDnsName = "contoso.test",
            DefaultNamingContext = "DC=contoso,DC=test",
            ConfigurationNamingContext = "CN=Configuration,DC=contoso,DC=test",
            SchemaNamingContext = "CN=Schema,CN=Configuration,DC=contoso,DC=test",
            BoundIdentity = userName,
            Authentication = AuthenticationMode.Credentials,
        };
        await using var session = new LdapDirectorySession(info, pool, channelEncrypted: true, NullLogger.Instance, TimeProvider.System);

        using var logon = SessionLogons.Of(session);

        Assert.Equal(user, logon.UserName);
        Assert.Equal(domain, logon.Domain);
        Assert.Equal("Zażółć!2026", new NetworkCredential(string.Empty, logon.Password).Password);
    }
}
