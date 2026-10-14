using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Directory;
using AdMng.Watchdog.IntegrationTests.Infrastructure;

[assembly: AssemblyFixture(typeof(LabFixture))]

namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>
/// The run's throwaway OU <c>OU=WatchdogIT-&lt;run id&gt;</c> below the lab domain (spec 22.4): created before the first
/// test, tree-deleted after the last one; leftovers of aborted runs older than <see cref="StaleAfter"/> are removed first
/// (younger ones may belong to a run in progress on the shared lab). Changes are made with an Administrator session
/// (<see cref="Writer"/>); the watchdog engines read with their own sessions (<see cref="EngineHost"/>). Seeded lab data
/// is never changed.
/// </summary>
public sealed class LabFixture : IAsyncLifetime
{
    public const string OuPrefix = "WatchdogIT-";

    /// <summary>Test OUs of other runs younger than this are left alone.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    private IDirectorySession? _admin;
    private IDirectorySession? _adminTls;
    private LabWriter? _writer;

    /// <summary>DN of this run's OU.</summary>
    public string RunOu { get; } = DistinguishedName.Parse(WatchdogLab.DomainDn)
        .Child("OU", OuPrefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6])
        .ToString();

    /// <summary>Administrator writes (plain LDAP; passwords over LDAPS).</summary>
    public LabWriter Writer => _writer ?? throw new InvalidOperationException(WatchdogLab.SkipReason);

    /// <summary>Administrator session over plain LDAP (reads of the test arrangement).</summary>
    public IDirectorySession Admin => _admin ?? throw new InvalidOperationException(WatchdogLab.SkipReason);

    public async ValueTask InitializeAsync()
    {
        if (!WatchdogLab.IsConfigured)
        {
            return;
        }

        _admin = await ConnectAsync(WatchdogLab.Plain(WatchdogLab.AdminUser), WatchdogLab.AdminPassword, CancellationToken.None);
        _adminTls = await ConnectAsync(WatchdogLab.Tls(WatchdogLab.AdminUser), WatchdogLab.AdminPassword, CancellationToken.None);
        _writer = new LabWriter(_admin, _adminTls);
        await RemoveStaleRunsAsync(CancellationToken.None);
        await _writer.CreateOuAsync(WatchdogLab.DomainDn, RunOu[3..RunOu.IndexOf(',', StringComparison.Ordinal)],
            "AdMng Watchdog integration tests – safe to delete", CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_admin is null)
        {
            return;
        }

        try
        {
            // Samba needs a while to tree-delete groups with 1600 linked members: a longer timeout than the default.
            var options = WatchdogLab.Plain(WatchdogLab.AdminUser) with { RequestTimeout = TimeSpan.FromMinutes(10) };
            await using var cleanup = await ConnectAsync(options, WatchdogLab.AdminPassword, CancellationToken.None);
            if (await cleanup.GetAsync(RunOu, [], CancellationToken.None) is not null)
            {
                await cleanup.DeleteAsync(RunOu, subtree: true, CancellationToken.None);
            }
        }
        finally
        {
            await _admin.DisposeAsync();
            if (_adminTls is not null)
            {
                await _adminTls.DisposeAsync();
            }
        }
    }

    /// <summary>A new OU below the run's OU (one per test or test class).</summary>
    public Task<string> CreateTestOuAsync(string name, CancellationToken cancellationToken) =>
        Writer.CreateOuAsync(RunOu, TestNames.Unique(name), null, cancellationToken);

    /// <summary>Opens a session (the caller disposes it).</summary>
    public static Task<IDirectorySession> ConnectAsync(ConnectionOptions options, string password, CancellationToken cancellationToken) =>
        new LdapDirectoryConnector().ConnectAsync(options, WatchdogLab.Password(password), cancellationToken);

    private async Task RemoveStaleRunsAsync(CancellationToken cancellationToken)
    {
        var stale = new List<string>();
        await foreach (var ou in Admin.SearchAsync(new DirectorySearch
        {
            BaseDn = WatchdogLab.DomainDn,
            Scope = SearchScope.OneLevel,
            Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.OrganizationalUnit), LdapFilter.StartsWith("ou", OuPrefix)),
            Attributes = [Attr.WhenCreated],
        }, cancellationToken))
        {
            if (ou.GetGeneralizedTime(Attr.WhenCreated) is { } created && DateTimeOffset.UtcNow - created > StaleAfter)
            {
                stale.Add(ou.DistinguishedName);
            }
        }

        foreach (var dn in stale)
        {
            await Admin.DeleteAsync(dn, subtree: true, cancellationToken);
        }
    }
}
