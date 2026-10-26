using System.Diagnostics;
using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Directory;
using AdMng.Directory.IntegrationTests.Infrastructure;
using AdMng.Watchdog.IntegrationTests.Infrastructure;

[assembly: AssemblyFixture(typeof(LabFixture))]

namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>
/// The run's throwaway OU <c>OU=WatchdogIT-&lt;run id&gt;</c> below the lab domain (spec 22.4), its description naming the
/// test process (<see cref="LabRuns"/>): created before the first test, removed after the last one. Before that, OUs of
/// runs that are over – the test process on this host ended, or the OU is older than <see cref="StaleAfter"/> – are
/// removed; OUs of runs still in progress on the shared lab are left alone. Trees are deleted client-side in short
/// requests (<see cref="LabCleanup"/>): a Samba tree delete of a group with 1600 linked members runs into its 300-s
/// request limit and blocks every other suite meanwhile. Changes are made with an Administrator session
/// (<see cref="Writer"/>); the watchdog engines read with their own sessions (<see cref="EngineHost"/>). Seeded lab data
/// is never changed.
/// </summary>
public sealed class LabFixture : IAsyncLifetime
{
    public const string OuPrefix = "WatchdogIT-";

    /// <summary>Test OUs of runs on other hosts younger than this are left alone.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    private IDirectorySession? _admin;
    private IDirectorySession? _adminTls;
    private LabWriter? _writer;

    /// <summary>The run OUs of this project on the lab.</summary>
    internal static LabRuns Runs { get; } = new(WatchdogLab.DomainDn, OuPrefix, "AdMng Watchdog integration tests", StaleAfter)
    {
        Server = WatchdogLab.Server ?? "the lab DC",
        Log = message => TestContext.Current.SendDiagnosticMessage(message),
    };

    /// <summary>Name of this run's OU: <c>WatchdogIT-&lt;UTC time&gt;-&lt;random&gt;</c>.</summary>
    public string RunOuName { get; } = Runs.NewRunName();

    /// <summary>DN of this run's OU.</summary>
    public string RunOu => DistinguishedName.Parse(WatchdogLab.DomainDn).Child("OU", RunOuName).ToString();

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

        try
        {
            _admin = await ConnectAsync(WatchdogLab.Plain(WatchdogLab.AdminUser), WatchdogLab.AdminPassword, CancellationToken.None);
            _adminTls = await ConnectAsync(WatchdogLab.Tls(WatchdogLab.AdminUser), WatchdogLab.AdminPassword, CancellationToken.None);
            _writer = new LabWriter(_admin, _adminTls);

            // Leftovers of runs that are over, with a generous timeout per request (another suite may be holding the DC).
            // Failures do not stop this run: it works in its own OU.
            await using (var cleanup = await ConnectAsync(CleanupOptions(), WatchdogLab.AdminPassword, CancellationToken.None))
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
            await using var cleanup = await ConnectAsync(CleanupOptions(), WatchdogLab.AdminPassword, CancellationToken.None);
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

    /// <summary>The administrator over plain LDAP with <see cref="LabCleanup.RequestTimeout"/> per request.</summary>
    private static ConnectionOptions CleanupOptions() =>
        WatchdogLab.Plain(WatchdogLab.AdminUser) with { RequestTimeout = LabCleanup.RequestTimeout };
}
