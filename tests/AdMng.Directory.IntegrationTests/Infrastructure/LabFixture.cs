using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Directory.IntegrationTests.Infrastructure;

[assembly: AssemblyFixture(typeof(LabFixture))]

namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// Shared administrator session plus the dedicated OU (<see cref="LabSettings.TestOu"/>) that is created
/// before the first test and removed with a tree delete after the last one. Seeded lab data is never changed.
/// </summary>
public sealed class LabFixture : IAsyncLifetime
{
    private IDirectorySession? _admin;

    /// <summary>Administrator session over plain LDAP (simple bind, lab only).</summary>
    public IDirectorySession Admin => _admin ?? throw new InvalidOperationException(LabSettings.SkipReason);

    public string TestOu => LabSettings.TestOu;

    public async ValueTask InitializeAsync()
    {
        if (!LabSettings.IsConfigured)
        {
            return;
        }

        _admin = await ConnectAsync(LabSettings.Plain(LabSettings.AdminUser), LabSettings.AdminPassword);

        // Leftovers of an aborted earlier run.
        if (await _admin.GetAsync(TestOu, [], CancellationToken.None) is not null)
        {
            await _admin.DeleteAsync(TestOu, subtree: true, CancellationToken.None);
        }

        await _admin.AddAsync(
            TestOu,
            new Dictionary<string, IReadOnlyList<object>>
            {
                [Attr.ObjectClass] = ["top", ObjectClasses.OrganizationalUnit],
                [Attr.Description] = ["AD_MNG integration tests - safe to delete"],
            },
            CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_admin is null)
        {
            return;
        }

        try
        {
            // Samba needs a while to tree-delete thousands of linked objects - allow more than the default timeout.
            var options = LabSettings.Plain(LabSettings.AdminUser) with { RequestTimeout = TimeSpan.FromMinutes(10) };
            await using var cleanup = await ConnectAsync(options, LabSettings.AdminPassword);
            await cleanup.DeleteAsync(TestOu, subtree: true, CancellationToken.None);
        }
        finally
        {
            await _admin.DisposeAsync();
        }
    }

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
