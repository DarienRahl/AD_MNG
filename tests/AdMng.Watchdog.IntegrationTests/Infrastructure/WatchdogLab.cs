using System.Net;
using AdMng.Core.Directory;

namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>
/// Connection data of the Samba lab (tools/testlab/README.md). Tests are skipped unless ADMNG_TEST_LDAP_SERVER is set.
/// The watchdog reads with the read-only account (ADMNG_TEST_LDAP_READONLY_*), changes are made as Administrator.
/// </summary>
internal static class WatchdogLab
{
    public const string SkipReason = "ADMNG_TEST_LDAP_SERVER is not set - see tools/testlab/README.md.";

    public static string? Server => Environment.GetEnvironmentVariable("ADMNG_TEST_LDAP_SERVER") is { Length: > 0 } s ? s : null;

    public static bool IsConfigured => Server is not null;

    public static string AdminUser => Env("ADMNG_TEST_LDAP_USER", "Administrator@contoso.test");

    public static string AdminPassword => Env("ADMNG_TEST_LDAP_PASSWORD", "P@ssw0rd!2026");

    public static string ReadOnlyUser => Env("ADMNG_TEST_LDAP_READONLY_USER", "odczyt.test@contoso.test");

    public static string ReadOnlyPassword => Env("ADMNG_TEST_LDAP_READONLY_PASSWORD", "Passw0rd!Lab2026");

    public static string DomainDns => Env("ADMNG_TEST_LDAP_DOMAIN", "contoso.test");

    /// <summary>Skips the calling test when no lab is configured.</summary>
    public static void RequireLab() => Assert.SkipUnless(IsConfigured, SkipReason);

    /// <summary>Simple bind over plain LDAP (lab only - the lab allows it explicitly).</summary>
    public static ConnectionOptions Plain(string userName) => new()
    {
        Server = Server,
        Domain = DomainDns,
        Authentication = AuthenticationMode.SimpleBind,
        Security = TransportSecurity.None,
        AllowInsecureSimpleBind = true,
        UserName = userName,
        RequestTimeout = TimeSpan.FromSeconds(60),
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };

    public static NetworkCredential Password(string password) => new(string.Empty, password);

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}
