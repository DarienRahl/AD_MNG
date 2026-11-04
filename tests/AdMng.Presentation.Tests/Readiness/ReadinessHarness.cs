using System.Globalization;
using System.Runtime.CompilerServices;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Readiness;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Presentation.Watchdog;
using AdMng.Testing;
using AdMng.Testing.EventLogs;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Readiness;

/// <summary>
/// The readiness check over a healthy sample domain (Polish UI): the sample company with the Watchdog infrastructure (dc01 =
/// the window's DC, PDC emulator and global catalog; dc02 a second DC), the Recycle Bin, DFSR for SYSVOL, the fake SYSVOL
/// with both default GPOs, a central store (en-US, pl-PL) and a Policies ACL that lets Domain Admins write; the event logs of
/// both DCs (dc01 reaching nine days back with every audited event, dc02 two days without any), WAW-PC001 as a reachable
/// test computer (DNS, WinRM, System log), the Watchdog of the sample status, classic Outlook and a supported Windows 11
/// with WebView2. The time is <see cref="EventLogSamples.Morning"/> + 3 h. Each test changes what it checks.
/// </summary>
internal sealed class ReadinessHarness
{
    public const string Dc01 = "dc01.contoso.test";
    public const string Dc02 = "dc02.contoso.test";
    public const string TestComputer = "waw-pc001.contoso.test";
    public const string WatchdogUrl = "https://admng-wd.contoso.com:8443";

    /// <summary>Modify (read, write, execute, delete) on a folder.</summary>
    public const int Modify = 0x001301BF;

    /// <summary>Read and execute on a folder.</summary>
    public const int ReadAndExecute = 0x001200A9;

    private ReadinessHarness(B2Harness b2)
    {
        B2 = b2;
        Builder.AddWatchdogInfrastructure();
        Builder.AddDomainController(Dc02, new Guid("2f0f0f0f-1111-2222-3333-444455556666"));
        Session.SetValues(Builder.PdcNtdsSettingsDn!, "options", "1");
        Session.ComputesTokenGroups = true;
        Session.Seed(DfsrGlobalSettingsDn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "msDFSR-GlobalSettings" },
            ["msDFSR-Flags"] = 48,
        });
        b2.Time.SetUtcNow(EventLogSamples.Morning.AddHours(3));

        Sysvol.AddGpoFolder(GpoInfo.DefaultDomainPolicyId, 3);
        Sysvol.AddGpoFolder(GpoInfo.DefaultDomainControllersPolicyId, 1);
        Sysvol.PutPolicyDefinition("windows.admx", "<policyDefinitions />"u8.ToArray());
        Sysvol.PutPolicyDefinition("inetres.admx", "<policyDefinitions />"u8.ToArray());
        Sysvol.PutPolicyDefinition("pl-PL/windows.adml", "<policyDefinitionResources />"u8.ToArray());
        Sysvol.PutPolicyDefinition("en-US/windows.adml", "<policyDefinitionResources />"u8.ToArray());
        Sysvol.PoliciesSecurity = PoliciesAcl();

        var now = Now;
        Connector.AddHost(Dc01, EventLogSamples.SecurityChannel, EventLogSamples.SystemChannel).AddHost(Dc02, EventLogSamples.SecurityChannel)
            .AddHost(TestComputer, EventLogSamples.SystemChannel);
        Connector.SetCoverage(Dc01, EventLogSamples.SecurityChannel, Coverage(Dc01, EventLogSamples.SecurityChannel, now.AddDays(-9)));
        Connector.SetCoverage(Dc02, EventLogSamples.SecurityChannel, Coverage(Dc02, EventLogSamples.SecurityChannel, now.AddDays(-2)));
        Connector.SetCoverage(TestComputer, EventLogSamples.SystemChannel, Coverage(TestComputer, EventLogSamples.SystemChannel, now.AddDays(-30)));
        Connector.AddRecords(Dc01,
        [
            EventLogSamples.AccountLockedOut(Dc01, 1, now.AddHours(-30), EventLogSamples.JanKowalski, EventLogSamples.WawPc001),
            EventLogSamples.KerberosPreAuthFailed(Dc01, 2, now.AddHours(-31), EventLogSamples.JanKowalski, EventLogSamples.WawPc001Ip),
            EventLogSamples.CredentialValidation(Dc01, 3, now.AddHours(-32), EventLogSamples.ZanetaSzymanska, EventLogSamples.WawPc002),
            EventLogSamples.LogonFailed(Dc01, 4, now.AddHours(-33), EventLogSamples.GrazynaDabrowska, EventLogSamples.KrkLt001Ip, EventLogSamples.KrkLt001),
            EventLogSamples.TicketGranted(Dc01, 5, now.AddHours(-34), "nieistniejący.użytkownik", EventLogSamples.VpnIp, status: "0x6"),
        ]);

        Resolver.Add(TestComputer, "10.10.1.21");
        Remote.Computers[TestComputer] = new FakeRemoteComputer();
        Watchdog = new FakeWatchdogClient(b2.Time);
        WatchdogConnection = new WatchdogConnectionService(Watchdog, b2.Settings, b2.Localizer);
        GroupPolicy = new GroupPolicyServices(b2.Services, b2.Engines, new GroupPolicyPlanner(new GroupPolicyReader(), b2.Time), new ScriptedGroupPolicyDialogs(), Sysvol);
        EventLogs = new EventLogServices(b2.Services, new CsvCodec(), Launcher, Connector);
    }

    public B2Harness B2 { get; }

    public FakeDirectoryBuilder Builder => B2.Builder;

    public FakeDirectorySession Session => B2.Session;

    public SampleCompany Company => B2.Company;

    public DateTimeOffset Now => B2.Time.GetUtcNow();

    public FakeSysvol Sysvol { get; } = new();

    public FakeEventLogConnector Connector { get; } = new();

    public EventLogLauncher Launcher { get; } = new();

    public FakeHostAddressResolver Resolver { get; } = new();

    public FakeRemoteComputerClient Remote { get; } = new();

    public FakeWatchdogClient Watchdog { get; }

    public WatchdogConnectionService WatchdogConnection { get; }

    public FakeOutlookClient Outlook { get; } = new();

    public FakeComputerInfo Computer { get; } = new();

    public GroupPolicyServices GroupPolicy { get; }

    public EventLogServices EventLogs { get; }

    public ReadinessResults Results { get; } = new();

    public string DfsrGlobalSettingsDn => $"CN=DFSR-GlobalSettings,{Builder.SystemDn}";

    /// <summary>The services of the check: every feature present unless a test leaves it out.</summary>
    public ReadinessServices Services(bool sysvol = true, bool eventLogs = true, bool remote = true, bool watchdog = true, bool outlook = true) => new(
        B2.Services,
        Results,
        new CsvCodec(),
        new WindowsReleaseCatalog(),
        new GroupPolicyReader(),
        sysvol ? GroupPolicy : null,
        eventLogs ? EventLogs : null,
        remote ? Remote : null,
        remote ? Resolver : null,
        watchdog ? WatchdogConnection : null,
        outlook ? Outlook : null,
        Computer);

    /// <summary>The window connected to the sample domain on dc01 (Polish UI), the Watchdog address set.</summary>
    public static async Task<ReadinessHarness> CreateAsync(string culture = "pl-PL")
    {
        var b2 = await B2Harness.CreateOnDomainControllerAsync(Dc01, CultureInfo.GetCultureInfo(culture));
        var harness = new ReadinessHarness(b2);
        await b2.Settings.UpdateAsync(s => s.Watchdog.ServiceUrl = WatchdogUrl);
        return harness;
    }

    /// <summary>Connects the window again through another session of the same directory (another DC, other facts, a skewed clock).</summary>
    public Task ReconnectAsync(string server = Dc01, Func<SessionInfo, SessionInfo>? info = null, TimeSpan? clockOffset = null)
    {
        IDirectorySession session = new FakeDomainControllerSession(Session, server, info);
        if (clockOffset is { } offset)
        {
            session = new SkewedClockSession(session, offset);
        }

        return B2.ReconnectAsync(session);
    }

    /// <summary>Runs a check through the checker (no view model) on the window's session.</summary>
    public Task<ReadinessReport> RunAsync(ReadinessDepth depth, string? testComputer = TestComputer, ReadinessServices? services = null) =>
        new ReadinessChecker(services ?? Services()).RunAsync(B2.Sessions.Current!, new ReadinessRequest(depth, testComputer), null, TestContext.Current.CancellationToken);

    /// <summary>The default ACL of Policies: Authenticated Users read, SYSTEM, Administrators and Domain Admins full, creators modify.</summary>
    public byte[] PoliciesAcl() => FakeSecurityDescriptor.Build(
        "S-1-5-32-544",
        null,
        new FakeAce(FakeAce.AccessAllowed, 0x02, ReadAndExecute, "S-1-5-11"),
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x001F01FF, "S-1-5-18"),
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x001F01FF, "S-1-5-32-544"),
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x001F01FF, $"{Builder.DomainSid}-512"),
        new FakeAce(FakeAce.AccessAllowed, 0x02, Modify, $"{Builder.DomainSid}-520"));

    public static EventLogCoverage Coverage(string host, string channel, DateTimeOffset oldest) => new()
    {
        Host = host,
        Channel = channel,
        Oldest = oldest,
        Newest = EventLogSamples.Morning.AddHours(3),
        RecordCount = 412_877,
        FileSizeBytes = 128L * 1024 * 1024,
        MaximumSizeBytes = 128L * 1024 * 1024,
        Mode = EventLogMode.Circular,
    };
}

/// <summary>A result of a report by its code (and host for the per-host checks).</summary>
internal static class ReadinessReportExtensions
{
    public static ReadinessResult Single(this ReadinessReport report, string code, string? host = null) =>
        Assert.Single(report.Results, r => r.Code == code && (host is null || string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase)));

    public static ReadinessStatus Status(this ReadinessReport report, string code, string? host = null) => report.Single(code, host).Status;
}

/// <summary>This computer as the test sets it (default: a supported Windows 11 24H2 Enterprise with WebView2).</summary>
internal sealed class FakeComputerInfo : ILocalComputerInfo
{
    public bool IsWindows { get; set; } = true;

    public string OperatingSystem { get; set; } = "Windows 11 Enterprise";

    public int? Build { get; set; } = 26100;

    public int? UpdateBuildRevision { get; set; } = 4652;

    public bool CanProbeWebView2 { get; set; } = true;

    public bool IsWebView2Disabled { get; set; }

    public string? WebView2Version { get; set; } = "142.0.3595.53";
}

/// <summary>A session whose RootDSE reports a clock <paramref name="offset"/> away from the test clock.</summary>
internal sealed class SkewedClockSession(IDirectorySession inner, TimeSpan offset) : IDirectorySession
{
    public SessionInfo Info => inner.Info;

    public ISchemaCatalog Schema => inner.Schema;

    public async Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken)
    {
        var found = await inner.GetAsync(distinguishedName, attributes, cancellationToken).ConfigureAwait(false);
        return distinguishedName.Length == 0 && found?.GetGeneralizedTime(Attr.CurrentTime) is { } time
            ? found.With([new AttributeValues(Attr.CurrentTime, [AdConvert.ToGeneralizedTime(time + offset)])])
            : found;
    }

    public async IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var found in inner.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            yield return found;
        }
    }

    public Task<VirtualListPage> GetVirtualListPageAsync(DirectorySearch search, VirtualListRequest window, CancellationToken cancellationToken) =>
        inner.GetVirtualListPageAsync(search, window, cancellationToken);

    public IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, CancellationToken cancellationToken) =>
        inner.GetAllValuesAsync(distinguishedName, attribute, cancellationToken);

    public Task<EffectivePermissions> GetEffectivePermissionsAsync(string distinguishedName, CancellationToken cancellationToken) =>
        inner.GetEffectivePermissionsAsync(distinguishedName, cancellationToken);

    public Task ModifyAsync(string distinguishedName, IReadOnlyList<AttributeModification> modifications, ModifyOptions? options, CancellationToken cancellationToken) =>
        inner.ModifyAsync(distinguishedName, modifications, options, cancellationToken);

    public Task AddAsync(string distinguishedName, IReadOnlyDictionary<string, IReadOnlyList<object>> attributes, CancellationToken cancellationToken) =>
        inner.AddAsync(distinguishedName, attributes, cancellationToken);

    public Task DeleteAsync(string distinguishedName, bool subtree, CancellationToken cancellationToken) =>
        inner.DeleteAsync(distinguishedName, subtree, cancellationToken);

    public Task<string> MoveAsync(string distinguishedName, string? newParentDn, string? newRdn, CancellationToken cancellationToken) =>
        inner.MoveAsync(distinguishedName, newParentDn, newRdn, cancellationToken);

    public Task ResetPasswordAsync(string distinguishedName, string newPassword, CancellationToken cancellationToken) =>
        inner.ResetPasswordAsync(distinguishedName, newPassword, cancellationToken);

    public Task RestoreDeletedAsync(string deletedObjectDn, string targetDn, CancellationToken cancellationToken) =>
        inner.RestoreDeletedAsync(deletedObjectDn, targetDn, cancellationToken);

    public Task<IAsyncDisposable?> WatchAsync(string containerDn, Action<string> onChanged, CancellationToken cancellationToken) =>
        inner.WatchAsync(containerDn, onChanged, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
