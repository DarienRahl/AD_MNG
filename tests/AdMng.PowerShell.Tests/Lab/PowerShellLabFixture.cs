using System.Diagnostics;
using System.Globalization;
using System.Net;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Directory;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.PowerShell.Tests.Lab;

/// <summary>
/// Connection data of the Samba lab (tools/testlab/README.md) for the lab tests of the read commands. They are skipped unless
/// ADMNG_TEST_LDAP_SERVER is set; the CI lab job sets the lab variables for every step, so its step of the engine's tests runs
/// them. The accounts and the domain can be overridden as for <c>AdMng.Directory.IntegrationTests</c>.
/// </summary>
internal static class PowerShellLab
{
    public const string SkipReason = "ADMNG_TEST_LDAP_SERVER is not set - see tools/testlab/README.md.";

    public static string? Server => Environment.GetEnvironmentVariable("ADMNG_TEST_LDAP_SERVER") is { Length: > 0 } s ? s : null;

    public static bool IsConfigured => Server is not null;

    public static string AdminUser => Env("ADMNG_TEST_LDAP_USER", "Administrator@contoso.test");

    public static string AdminPassword => Env("ADMNG_TEST_LDAP_PASSWORD", "P@ssw0rd!2026");

    public static string DomainDn => Env("ADMNG_TEST_LDAP_BASE", "DC=contoso,DC=test");

    public static string DomainDns => Env("ADMNG_TEST_LDAP_DOMAIN", "contoso.test");

    /// <summary>Seeded company tree (read-only for the tests).</summary>
    public static string CompanyOu => $"OU=Firma,{DomainDn}";

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

    /// <summary>A unique name, e.g. <c>Zagnieżdżone 3f2a9c1b</c>, so that test classes running in parallel never collide.</summary>
    public static string Unique(string prefix) => $"{prefix} {Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>A 20 character sAMAccountName (AD limit), recognisable as one of these tests.</summary>
    public static string SamAccountName() => "ps" + Guid.NewGuid().ToString("N")[..18];

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}

/// <summary>
/// The administrator session of the read commands' lab tests and this run's own OU <c>OU=AdMngPowerShell-&lt;UTC time&gt;-&lt;random&gt;</c>
/// directly below the lab domain, its description naming the test process (<see cref="LabRuns"/>): created before the first test
/// of the class and removed after its last one. Before that, the OUs of runs that are over are removed, as in the fixtures of
/// <c>AdMng.Directory.IntegrationTests</c> and <c>AdMng.Watchdog.IntegrationTests</c> (whose run OU and cleanup code this one
/// shares as linked files). Seeded lab data is only read. Without the lab variables nothing is contacted.
/// </summary>
public sealed class PowerShellLabFixture : IAsyncLifetime
{
    public const string OuPrefix = "AdMngPowerShell-";

    /// <summary>Run OUs of runs on other hosts younger than this are left alone.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    private IDirectorySession? _admin;

    /// <summary>The run OUs of this project on the lab.</summary>
    internal static LabRuns Runs { get; } = new(PowerShellLab.DomainDn, OuPrefix, "AD_MNG PowerShell engine lab tests", StaleAfter)
    {
        Server = PowerShellLab.Server ?? "the lab DC",
        Log = message => TestContext.Current.SendDiagnosticMessage(message),
    };

    /// <summary>Name of this run's OU.</summary>
    public string RunOuName { get; } = Runs.NewRunName();

    /// <summary>DN of this run's OU, the only place where the tests create objects.</summary>
    public string RunOu => DistinguishedName.Parse(PowerShellLab.DomainDn).Child("OU", RunOuName).ToString();

    /// <summary>Administrator session over plain LDAP (simple bind, lab only).</summary>
    public IDirectorySession Admin => _admin ?? throw new InvalidOperationException(PowerShellLab.SkipReason);

    public async ValueTask InitializeAsync()
    {
        if (!PowerShellLab.IsConfigured)
        {
            return;
        }

        try
        {
            _admin = await ConnectAsync(PowerShellLab.Plain(PowerShellLab.AdminUser));

            // Leftovers of runs that are over, with a generous timeout per request (another suite may be holding the DC).
            // Failures do not stop this run: it works in its own OU.
            await using (var cleanup = await ConnectAsync(CleanupOptions()))
            {
                await Runs.RemoveStaleAsync(cleanup, CancellationToken.None);
            }

            await Runs.CreateRunOuAsync(_admin, RunOuName, CancellationToken.None);
        }
        catch (DirectoryException ex) when (Runs.Explain(ex, "preparing the run OU") is var explained && !ReferenceEquals(explained, ex))
        {
            throw explained;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_admin is null)
        {
            return;
        }

        try
        {
            await using var cleanup = await ConnectAsync(CleanupOptions());
            var stopwatch = Stopwatch.StartNew();
            var deleted = await LabCleanup.DeleteTreeAsync(cleanup, RunOu, CancellationToken.None);
            TestContext.Current.SendDiagnosticMessage(string.Create(
                CultureInfo.InvariantCulture, $"Removed {RunOu}: {deleted} objects in {stopwatch.Elapsed.TotalSeconds:F0} s."));
        }
        catch (DirectoryException ex) when (Runs.Explain(ex, $"deleting {RunOu} (the next run removes it)") is var explained && !ReferenceEquals(explained, ex))
        {
            throw explained;
        }
        finally
        {
            await _admin.DisposeAsync();
        }
    }

    /// <summary>Creates an OU below this run's OU and returns its DN.</summary>
    public async Task<string> CreateOuAsync(string name)
    {
        var dn = DistinguishedName.Parse(RunOu).Child("OU", name).ToString();
        await AddCheckedAsync(
            dn,
            new Dictionary<string, IReadOnlyList<object>> { [Attr.ObjectClass] = ["top", ObjectClasses.OrganizationalUnit] },
            TestContext.Current.CancellationToken);
        return dn;
    }

    /// <summary>Creates a (disabled, password-less) user and returns its DN.</summary>
    public async Task<string> CreateUserAsync(string parentDn, string cn)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("CN", cn).ToString();
        await AddCheckedAsync(
            dn,
            new Dictionary<string, IReadOnlyList<object>>
            {
                [Attr.ObjectClass] = ["top", "person", "organizationalPerson", ObjectClasses.User],
                [Attr.SamAccountName] = [PowerShellLab.SamAccountName()],
            },
            TestContext.Current.CancellationToken);
        return dn;
    }

    /// <summary>
    /// Adds a test object. The lab DC sometimes drops a connection under parallel writes (LDAP 81) and the session never re-sends
    /// a write that may have reached the server, so after a dropped connection the object is looked up and added again only when
    /// it is not there (as the fixture of <c>AdMng.Directory.IntegrationTests</c> does).
    /// </summary>
    private async Task AddCheckedAsync(string dn, IReadOnlyDictionary<string, IReadOnlyList<object>> attributes, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Admin.AddAsync(dn, attributes, cancellationToken);
                return;
            }
            catch (DirectoryException ex) when (attempt < 4 && ex.Kind is DirectoryErrorKind.ServerUnavailable or DirectoryErrorKind.Busy or DirectoryErrorKind.Timeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
                if (await Admin.GetAsync(dn, [Attr.ObjectClass], cancellationToken) is not null)
                {
                    return; // the first add reached the DC
                }
            }
        }
    }

    private static Task<IDirectorySession> ConnectAsync(ConnectionOptions options) =>
        new LdapDirectoryConnector().ConnectAsync(options, PowerShellLab.Password(PowerShellLab.AdminPassword), CancellationToken.None);

    /// <summary>The administrator over plain LDAP with <see cref="LabCleanup.RequestTimeout"/> per request.</summary>
    private static ConnectionOptions CleanupOptions() =>
        PowerShellLab.Plain(PowerShellLab.AdminUser) with { RequestTimeout = LabCleanup.RequestTimeout };
}
