using System.Net;
using AdMng.Core.Directory;

namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// Connection data of the test lab (tools/testlab/README.md). Integration tests are skipped unless
/// ADMNG_TEST_LDAP_SERVER is set. The lab accounts can be overridden with environment variables.
/// </summary>
internal static class LabSettings
{
    public const string SkipReason = "ADMNG_TEST_LDAP_SERVER is not set - see tools/testlab/README.md.";

    public static string? Server => Environment.GetEnvironmentVariable("ADMNG_TEST_LDAP_SERVER") is { Length: > 0 } s ? s : null;

    public static bool IsConfigured => Server is not null;

    public static string AdminUser => Env("ADMNG_TEST_LDAP_USER", "Administrator@contoso.test");

    public static string AdminPassword => Env("ADMNG_TEST_LDAP_PASSWORD", "P@ssw0rd!2026");

    public static string HelpdeskUser => Env("ADMNG_TEST_LDAP_HELPDESK_USER", "helpdesk.test@contoso.test");

    public static string HelpdeskPassword => Env("ADMNG_TEST_LDAP_HELPDESK_PASSWORD", "Passw0rd!Lab2026");

    public static string ReaderUser => Env("ADMNG_TEST_LDAP_READER_USER", "odczyt.test@contoso.test");

    public static string ReaderPassword => Env("ADMNG_TEST_LDAP_READER_PASSWORD", "Passw0rd!Lab2026");

    public static string DomainDn => Env("ADMNG_TEST_LDAP_BASE", "DC=contoso,DC=test");

    public static string DomainDns => Env("ADMNG_TEST_LDAP_DOMAIN", "contoso.test");

    public static string NetBiosName => Env("ADMNG_TEST_LDAP_NETBIOS", "CONTOSO");

    /// <summary>Seeded company tree (read-only for the tests).</summary>
    public static string CompanyOu => $"OU=Firma,{DomainDn}";

    public static string EmployeesOu => $"OU=Pracownicy,{CompanyOu}";

    /// <summary>The only place where tests create, change and delete objects.</summary>
    public static string TestOu => $"OU=AdMngIntegration,{DomainDn}";

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

    /// <summary>Simple bind over LDAPS with the lab's self-signed certificate.</summary>
    public static ConnectionOptions Tls(string userName) => Plain(userName) with
    {
        Security = TransportSecurity.Tls,
        AllowInsecureSimpleBind = false,
        AllowUntrustedCertificate = true,
    };

    public static NetworkCredential Password(string password) => new(string.Empty, password);

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}
