using System.Net;
using AdMng.Core.Directory;
using AdMng.Directory;
using AdMng.Watchdog.Service.Directory;

namespace AdMng.Watchdog.Service.Tests.Directory;

/// <summary>Read-only checks against the Samba lab (skipped unless <c>ADMNG_TEST_LDAP_SERVER</c> is set): the PDC
/// emulator lookup of the session provider.</summary>
public sealed class LabDirectoryTests
{
    private static string? Server => Environment.GetEnvironmentVariable("ADMNG_TEST_LDAP_SERVER") is { Length: > 0 } s ? s : null;

    [Fact]
    public async Task PdcLookup_FindsTheDnsNameOfTheRoleOwner()
    {
        Assert.SkipUnless(Server is not null, "ADMNG_TEST_LDAP_SERVER is not set - see tools/testlab/README.md.");
        var ct = TestContext.Current.CancellationToken;
        var user = Environment.GetEnvironmentVariable("ADMNG_TEST_LDAP_READER_USER") is { Length: > 0 } u ? u : "odczyt.test@contoso.test";
        var password = Environment.GetEnvironmentVariable("ADMNG_TEST_LDAP_READER_PASSWORD") is { Length: > 0 } p ? p : "Passw0rd!Lab2026";
        var connector = new LdapDirectoryConnector();
        await using var session = await connector.ConnectAsync(
            new ConnectionOptions
            {
                Server = Server,
                Domain = "contoso.test",
                Authentication = AuthenticationMode.SimpleBind,
                Security = TransportSecurity.None,
                AllowInsecureSimpleBind = true,
                UserName = user,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            },
            new NetworkCredential(string.Empty, password),
            ct);

        var pdc = await ServiceDirectorySessionProvider.FindPdcAsync(session, ct);

        Assert.False(string.IsNullOrWhiteSpace(pdc));
        Assert.EndsWith(".contoso.test", pdc, StringComparison.OrdinalIgnoreCase);
    }
}
