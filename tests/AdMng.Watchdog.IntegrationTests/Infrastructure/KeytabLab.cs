using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Keytab;
using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>
/// The keytab environment of the Linux lab tests (<c>eval "$(tools/testlab/watchdog/keytab-lab.sh env)"</c>): the
/// keytab at <c>KRB5_CLIENT_KTNAME</c> (= <c>KRB5_KTNAME</c>, the acceptor's), <c>KRB5_CONFIG</c>, <c>LDAPCONF</c> and
/// an empty <c>MEMORY:</c> ccache, so the service account authenticates from the keytab the tests write. Fresh computer
/// accounts are created in the run's OU with a one-time password and handed to <c>admng-watchdog-keytab init</c>
/// in-process. Tests that write the keytab or use the process ccache run in <see cref="KeytabLabCollection"/>.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class KeytabLab
{
    public const string SkipReason = "The keytab lab environment is not set - eval \"$(tools/testlab/watchdog/keytab-lab.sh env)\" (tools/testlab/watchdog/README.md).";

    public static string KeytabPath => StartupChecks.KeytabPath(Environment.GetEnvironmentVariable("KRB5_CLIENT_KTNAME"));

    public static string Krb5Config => Environment.GetEnvironmentVariable("KRB5_CONFIG")!;

    /// <summary>The DC host name (Kerberos needs <c>ldap/&lt;fqdn&gt;</c>).</summary>
    public static string Server => Environment.GetEnvironmentVariable("ADMNG_TEST_GSSAPI_SERVER")!;

    public static string Realm => WatchdogLab.DomainDns.ToUpperInvariant();

    /// <summary>Skips unless Linux, the lab and the keytab environment are there; empties the process's MEMORY ccache (it
    /// may hold a ticket of an earlier test's account).</summary>
    public static void Require()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "MIT Kerberos and SASL GSSAPI are the Linux path.");
        WatchdogLab.RequireLab();
        Assert.SkipWhen(
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ADMNG_TEST_KEYTAB_DIR"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ADMNG_TEST_GSSAPI_SERVER"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KRB5_CLIENT_KTNAME"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KRB5_KTNAME"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KRB5_CONFIG"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LDAPCONF"))
            || Environment.GetEnvironmentVariable("KRB5CCNAME")?.StartsWith("MEMORY:", StringComparison.Ordinal) != true,
            SkipReason);
        NativeKerberos.DestroyDefaultCcache();
    }

    /// <summary>A computer account as design 4.2 creates it (disabled with the one-time password, then enabled): SPNs
    /// <c>HTTP/</c> and <c>HOST/</c>, AES only, NOT_DELEGATED – in a new OU below the run's OU.</summary>
    public static async Task<KeytabTestAccount> CreateAccountAsync(LabFixture lab, string otp, CancellationToken cancellationToken)
    {
        var ou = await lab.CreateTestOuAsync("Keytab", cancellationToken);
        var name = "WD" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
        var fqdn = name.ToLowerInvariant() + "." + WatchdogLab.DomainDns;
        var dn = DistinguishedName.Parse(ou).Child("CN", name).ToString();
        await lab.Admin.AddAsync(dn, new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", "person", "organizationalPerson", ObjectClasses.User, ObjectClasses.Computer],
            [Attr.SamAccountName] = [name + "$"],
            [Attr.UserAccountControl] = [Flags(UserAccountControl.WorkstationTrustAccount | UserAccountControl.AccountDisable | UserAccountControl.NotDelegated)],
            [Attr.DnsHostName] = [fqdn],
            [Attr.ServicePrincipalName] = ["HTTP/" + fqdn, "HOST/" + fqdn, "HOST/" + name],
            [Attr.SupportedEncryptionTypes] = ["24"],
        }, cancellationToken);

        await lab.Writer.ResetPasswordAsync(dn, otp, cancellationToken);
        await lab.Writer.SetAccountControlAsync(dn, UserAccountControl.WorkstationTrustAccount | UserAccountControl.NotDelegated, cancellationToken);
        return new KeytabTestAccount(ou, dn, name + "$", name + "$@" + Realm, fqdn);
    }

    /// <summary>Runs the keytab tool in-process against the test's keytab, configuration and state folder.</summary>
    public static (int Exit, string Output) RunTool(KeytabTempDirectory state, string[] args, string? stdin = null, Action<string>? fault = null)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes((stdin ?? string.Empty) + "\n"));
        var host = new KeytabToolHost
        {
            Out = output,
            Error = output,
            Input = input,
            InputIsTerminal = false,
            ReplicationPollInterval = TimeSpan.FromSeconds(1),
            FaultPoint = fault,
        };
        var common = new List<string>(args);
        if (!args.Contains("--krb5-config"))
        {
            common.AddRange(["--krb5-config", Krb5Config]);
        }

        common.AddRange(["--keytab", KeytabPath, "--state-dir", state.Path]);
        if (args[0] is "init" or "rotate")
        {
            common.AddRange(["--replication-timeout", "0"]);
        }

        var exit = KeytabTool.Run(common, host, null);
        return (exit, output.ToString());
    }

    /// <summary><c>init</c> of a fresh account; asserts success.</summary>
    public static async Task<KeytabTestAccount> CreateAndInitAsync(LabFixture lab, KeytabTempDirectory state, CancellationToken cancellationToken)
    {
        var otp = OneTimePassword();
        var account = await CreateAccountAsync(lab, otp, cancellationToken);
        var (exit, output) = RunTool(state, ["init", "--password-stdin", "--principal", account.Principal, "--fqdn", account.Fqdn], otp);
        Assert.True(exit == 0, output);
        return account;
    }

    public static string OneTimePassword() => "Otp-" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "-9a!";

    private static string Flags(UserAccountControl flags) => ((int)flags).ToString(CultureInfo.InvariantCulture);
}

/// <summary>A computer account of a keytab lab test.</summary>
internal sealed record KeytabTestAccount(string Ou, string Dn, string Name, string Principal, string Fqdn)
{
    public string HttpSpn => "HTTP/" + Fqdn;
}

/// <summary>The keytab tool's state folder of one test.</summary>
internal sealed class KeytabTempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Directory.CreateTempSubdirectory("admng-keytab-it-").FullName;

    public string StatusFile => System.IO.Path.Combine(Path, "status.json");

    public void Dispose() => System.IO.Directory.Delete(Path, recursive: true);
}

/// <summary>Tests that write the lab keytab (<c>$ADMNG_TEST_KEYTAB_DIR/admng-wd.keytab</c>) or use the process's MEMORY
/// ccache run one after another.</summary>
[CollectionDefinition(Name)]
public sealed class KeytabLabCollection
{
    public const string Name = "Keytab lab";
}
