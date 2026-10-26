using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Directory.IntegrationTests.Infrastructure;

[assembly: AssemblyFixture(typeof(LabFixture))]

namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// Shared administrator session plus this run's own OU (<see cref="LabSettings.TestOu"/>, unique per run, its
/// description naming the test process – <see cref="LabRuns"/>) that is created before the first test and removed after
/// the last one. Before that, OUs of runs that are over (the test process ended, or older than <see cref="StaleAfter"/>)
/// are removed; OUs of runs still in progress are left alone. Large trees are deleted client-side in short requests
/// (<see cref="LabCleanup"/>): a Samba tree delete of thousands of objects exceeds its 300-s request limit and blocks every
/// other suite meanwhile. Seeded lab data is never changed.
/// </summary>
public sealed class LabFixture : IAsyncLifetime
{
    /// <summary>Test OUs of runs on other hosts (or of earlier fixture versions) younger than this are left alone.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    private IDirectorySession? _admin;

    /// <summary>Administrator session over plain LDAP (simple bind, lab only).</summary>
    public IDirectorySession Admin => _admin ?? throw new InvalidOperationException(LabSettings.SkipReason);

    public string TestOu => LabSettings.TestOu;

    /// <summary>The run OUs of this project on the lab.</summary>
    internal static LabRuns Runs { get; } = new(
        LabSettings.DomainDn, LabSettings.RunOuPrefix, "AD_MNG integration tests", StaleAfter, LabSettings.LegacyTestOuName)
    {
        Server = LabSettings.Server ?? "the lab DC",
        Log = message => TestContext.Current.SendDiagnosticMessage(message),
    };

    public async ValueTask InitializeAsync()
    {
        if (!LabSettings.IsConfigured)
        {
            return;
        }

        try
        {
            _admin = await ConnectAsync(LabSettings.Plain(LabSettings.AdminUser), LabSettings.AdminPassword);

            // Leftovers of runs that are over, with a generous timeout per request (another suite may be holding the DC).
            // Failures do not stop this run: it works in its own OU.
            await using (var cleanup = await ConnectAsync(CleanupOptions(), LabSettings.AdminPassword))
            {
                await Runs.RemoveStaleAsync(cleanup, CancellationToken.None);
            }

            await Runs.CreateRunOuAsync(_admin, LabSettings.RunOuName, CancellationToken.None);
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
            await using var cleanup = await ConnectAsync(CleanupOptions(), LabSettings.AdminPassword);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var deleted = await LabCleanup.DeleteTreeAsync(cleanup, TestOu, CancellationToken.None);
            TestContext.Current.SendDiagnosticMessage(string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"Removed {TestOu}: {deleted} objects in {stopwatch.Elapsed.TotalSeconds:F0} s."));
        }
        catch (DirectoryException ex) when (Runs.Explain(ex, $"deleting {TestOu} (the next run removes it)") is var explained && !ReferenceEquals(explained, ex))
        {
            throw explained;
        }
        finally
        {
            await _admin.DisposeAsync();
        }
    }

    /// <summary>The administrator over plain LDAP with <see cref="LabCleanup.RequestTimeout"/> per request.</summary>
    private static ConnectionOptions CleanupOptions() =>
        LabSettings.Plain(LabSettings.AdminUser) with { RequestTimeout = LabCleanup.RequestTimeout };

    /// <summary>Opens a new session (the caller disposes it).</summary>
    internal static Task<IDirectorySession> ConnectAsync(
        ConnectionOptions options,
        string password,
        CapturingLoggerFactory? logs = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default) =>
        new LdapDirectoryConnector(logs, timeProvider).ConnectAsync(options, LabSettings.Password(password), cancellationToken);

    /// <summary>Creates an OU below the test OU (or below <paramref name="parentDn"/>) and returns its DN.</summary>
    public async Task<string> CreateOuAsync(string name, string? parentDn = null)
    {
        var dn = DistinguishedName.Parse(parentDn ?? TestOu).Child("OU", name).ToString();
        await Admin.AddAsync(
            dn,
            new Dictionary<string, IReadOnlyList<object>> { [Attr.ObjectClass] = ["top", ObjectClasses.OrganizationalUnit] },
            TestContext.Current.CancellationToken);
        return dn;
    }

    /// <summary>Creates a (disabled, password-less) user and returns its DN.</summary>
    public async Task<string> CreateUserAsync(string parentDn, string cn, IReadOnlyDictionary<string, string>? extra = null)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("CN", cn).ToString();
        var attributes = new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", "person", "organizationalPerson", ObjectClasses.User],
            [Attr.SamAccountName] = [TestNames.SamAccountName()],
        };
        foreach (var (name, value) in extra ?? new Dictionary<string, string>())
        {
            attributes[name] = [value];
        }

        await Admin.AddAsync(dn, attributes, TestContext.Current.CancellationToken);
        return dn;
    }

    /// <summary>Creates a contact (cheap object without security principal) and returns its DN.</summary>
    public async Task<string> CreateContactAsync(string parentDn, string cn)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("CN", cn).ToString();
        await Admin.AddAsync(
            dn,
            new Dictionary<string, IReadOnlyList<object>>
            {
                [Attr.ObjectClass] = ["top", "person", "organizationalPerson", ObjectClasses.Contact],
            },
            TestContext.Current.CancellationToken);
        return dn;
    }

    /// <summary>First seeded user below <paramref name="baseDn"/> (read only).</summary>
    public async Task<DirectoryObject> FindSeededUserAsync(string baseDn, params string[] attributes)
    {
        var search = new DirectorySearch
        {
            BaseDn = baseDn,
            Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectCategory, "person"), LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.User)),
            Attributes = attributes,
            SizeLimit = 1,
        };
        await foreach (var user in Admin.SearchAsync(search, TestContext.Current.CancellationToken))
        {
            return user;
        }

        throw new InvalidOperationException($"No seeded user below {baseDn}.");
    }
}
