using System.Runtime.Versioning;
using AdMng.Core.Directory;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// Kerberos (SASL GSSAPI) sessions on Linux – the AdMng Watchdog default (Linux design 5.1): a sealed session reports a
/// security strength factor ≥ 56, an unsatisfiable <c>minssf</c> refuses the bind in libldap, and a session that ends up
/// below 56 anyway (a missing or weakened <c>LDAPCONF</c>) is refused by the connection factory itself. LDAPS with
/// Kerberos runs without a SASL layer (<c>maxssf=0</c>, 5.2).
/// </summary>
/// <remarks>
/// Needs the DC host name in <c>ADMNG_TEST_GSSAPI_SERVER</c>, a Kerberos ticket in <c>KRB5CCNAME</c> and <c>LDAPCONF</c>
/// with <c>minssf=56</c> in the environment of the test process (libldap and libkrb5 read them natively):
/// <c>eval "$(tools/testlab/watchdog/keytab-lab.sh env --admin)"</c>. The SSF limits of single connections are set
/// through <see cref="LdapConnectionFactory.SaslSecurityLimitsForTests"/> (per async flow, so parallel tests are not
/// affected).
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class GssapiTests
{
    private const string SkipReason = "ADMNG_TEST_GSSAPI_SERVER is not set - see tools/testlab/watchdog/README.md (keytab-lab.sh env --admin).";

    private static string? Server => Environment.GetEnvironmentVariable("ADMNG_TEST_GSSAPI_SERVER") is { Length: > 0 } s ? s : null;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SealedSession_ReportsItsSecurityStrengthFactor()
    {
        RequireGssapiLab();

        await using var session = await new LdapDirectoryConnector().ConnectAsync(Kerberos(), null, Ct);
        var head = await session.GetAsync(LabSettings.DomainDn, ["distinguishedName"], Ct);

        Assert.NotNull(head);
        Assert.NotNull(session.Info.SecurityStrengthFactor);
        Assert.True(session.Info.SecurityStrengthFactor >= 56, $"SSF {session.Info.SecurityStrengthFactor}");
        Assert.Equal(AuthenticationMode.CurrentUser, session.Info.Authentication);
        Assert.Equal(TransportSecurity.SignAndSeal, session.Info.Security);
    }

    [Fact]
    public async Task UnsatisfiableMinimumSsf_IsRefusedByLibldap()
    {
        RequireGssapiLab();
        LdapConnectionFactory.SaslSecurityLimitsForTests.Value = (56, 0);
        try
        {
            await Assert.ThrowsAsync<DirectoryException>(() => new LdapDirectoryConnector().ConnectAsync(Kerberos(), null, Ct));
        }
        finally
        {
            LdapConnectionFactory.SaslSecurityLimitsForTests.Value = null;
        }
    }

    [Fact]
    public async Task SessionWithoutSecurityLayer_IsRefusedByTheSsfCheck()
    {
        RequireGssapiLab();
        LdapConnectionFactory.SaslSecurityLimitsForTests.Value = (0, 0);
        try
        {
            var error = await Assert.ThrowsAsync<DirectoryException>(() => new LdapDirectoryConnector().ConnectAsync(Kerberos(), null, Ct));

            Assert.Equal(DirectoryErrorKind.StrongAuthRequired, error.Kind);
            Assert.Contains("security strength factor of 0", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LdapConnectionFactory.SaslSecurityLimitsForTests.Value = null;
        }
    }

    [Fact]
    public async Task Ldaps_WithKerberos_UsesTlsWithoutASaslLayer()
    {
        RequireGssapiLab();

        // The LDAPS variant of the service's ldap.conf sets "minssf=0,maxssf=0" (5.2); the lab's certificate is self-signed.
        LdapConnectionFactory.SaslSecurityLimitsForTests.Value = (0, 0);
        try
        {
            await using var session = await new LdapDirectoryConnector().ConnectAsync(
                Kerberos() with { Security = TransportSecurity.Tls, AllowUntrustedCertificate = true },
                null,
                Ct);
            var head = await session.GetAsync(LabSettings.DomainDn, ["distinguishedName"], Ct);

            Assert.NotNull(head);
            Assert.Equal(636, session.Info.Port);
            Assert.Equal(0, session.Info.SecurityStrengthFactor);
        }
        finally
        {
            LdapConnectionFactory.SaslSecurityLimitsForTests.Value = null;
        }
    }

    private static ConnectionOptions Kerberos() => new()
    {
        Server = Server,
        Domain = LabSettings.DomainDns,
        Authentication = AuthenticationMode.CurrentUser,
        Security = TransportSecurity.SignAndSeal,
        RequestTimeout = TimeSpan.FromSeconds(60),
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };

    private static void RequireGssapiLab()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "SASL GSSAPI through libldap is the Linux path.");
        Assert.SkipUnless(Server is not null, SkipReason);
    }
}
