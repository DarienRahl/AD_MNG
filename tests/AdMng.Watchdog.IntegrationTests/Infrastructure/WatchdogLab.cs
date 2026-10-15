using System.Net;
using AdMng.Core.Directory;

namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>
/// Connection data of the Samba lab (tools/testlab/README.md, tools/testlab/watchdog/README.md). Tests are skipped
/// unless ADMNG_TEST_LDAP_SERVER is set. The watchdog reads with the read-only account (ADMNG_TEST_LDAP_READONLY_*),
/// changes are made as Administrator. ADMNG_TEST_SMTP_* / ADMNG_TEST_MAILPIT_API point the end-to-end test at Mailpit.
/// </summary>
internal static class WatchdogLab
{
    public const string SkipReason = "ADMNG_TEST_LDAP_SERVER is not set - see tools/testlab/watchdog/README.md.";

    public static string? Server => Environment.GetEnvironmentVariable("ADMNG_TEST_LDAP_SERVER") is { Length: > 0 } s ? s : null;

    public static bool IsConfigured => Server is not null;

    public static string AdminUser => Env("ADMNG_TEST_LDAP_USER", "Administrator@contoso.test");

    public static string AdminPassword => Env("ADMNG_TEST_LDAP_PASSWORD", "P@ssw0rd!2026");

    public static string ReadOnlyUser => Env("ADMNG_TEST_LDAP_READONLY_USER", "odczyt.test@contoso.test");

    public static string ReadOnlyPassword => Env("ADMNG_TEST_LDAP_READONLY_PASSWORD", "Passw0rd!Lab2026");

    public static string DomainDns => Env("ADMNG_TEST_LDAP_DOMAIN", "contoso.test");

    public static string DomainDn => "DC=" + DomainDns.Replace(".", ",DC=", StringComparison.Ordinal);

    /// <summary>NetBIOS name of the lab domain (the first label of the DNS name, upper case).</summary>
    public static string NetBiosName => DomainDns.Split('.')[0].ToUpperInvariant();

    /// <summary>SMTP sink of the end-to-end test (Mailpit); null = an in-process sink.</summary>
    public static string? SmtpHost => Environment.GetEnvironmentVariable("ADMNG_TEST_SMTP_HOST") is { Length: > 0 } h ? h : null;

    public static int SmtpPort => int.TryParse(Environment.GetEnvironmentVariable("ADMNG_TEST_SMTP_PORT"), out var port) ? port : 1025;

    /// <summary>Mailpit REST API (GET /api/v1/messages) when <see cref="SmtpHost"/> is Mailpit.</summary>
    public static Uri? MailpitApi => Environment.GetEnvironmentVariable("ADMNG_TEST_MAILPIT_API") is { Length: > 0 } a ? new Uri(a) : null;

    /// <summary>The optional real-Kerberos tests (tools/testlab/watchdog/krb5-lab.sh).</summary>
    public static bool KerberosEnabled => Environment.GetEnvironmentVariable("ADMNG_TEST_KERBEROS") == "1";

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

    /// <summary>Simple bind over LDAPS with the lab's self-signed certificate (password operations).</summary>
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
