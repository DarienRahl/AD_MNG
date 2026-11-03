using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Tests.AccountReview;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Testing;
using AdMng.Testing.EventLogs;

namespace AdMng.Presentation.Tests.EventLogs;

/// <summary>
/// The sample company with two DCs (dc01 = PDC emulator and the window's DC, dc02 read through its own short-lived
/// session with per-DC values), the lockout policy of the Watchdog infrastructure (5 bad passwords, 30 min), the time set to
/// <see cref="EventLogSamples.Morning"/> + 3 h, and a <see cref="FakeEventLogConnector"/> with both DCs' Security logs.
/// </summary>
internal sealed class EventLogHarness
{
    public const string Dc01 = "dc01.contoso.test";
    public const string Dc02 = "dc02.contoso.test";

    private EventLogHarness(B2Harness b2, bool withGroupPolicy, bool withRemote)
    {
        B2 = b2;
        b2.Builder.AddWatchdogInfrastructure();
        b2.Builder.AddDomainController(Dc02, new Guid("2f0f0f0f-1111-2222-3333-444455556666"));
        b2.Time.SetUtcNow(EventLogSamples.Morning.AddHours(3));
        b2.ConnectToServer = (_, server, _) => Task.FromResult<IDirectorySession>(new DcValuesSession(new FakeDomainControllerSession(b2.Session, server), DcValues(server)));
        Connector.AddHost(Dc01, "Security", "System").AddHost(Dc02, "Security");
        Remote = new FakeRemoteComputerClient();
        if (withGroupPolicy)
        {
            GroupPolicy = new GroupPolicyServices(b2.Services, b2.Engines, new GroupPolicyPlanner(new GroupPolicyReader(), b2.Time), new ScriptedGroupPolicyDialogs());
            GroupPolicy.DocumentRequested += (_, request) => GroupPolicyRequests.Add(request);
        }

        Services = new EventLogServices(b2.Services, new CsvCodec(), Launcher, Connector, new ServerConnector(b2), GroupPolicy,
            remote: withRemote ? Remote : null, nameLookup: Lookup);
        Launcher.Requested += (_, request) => Requests.Add(request);
    }

    public B2Harness B2 { get; }

    public FakeEventLogConnector Connector { get; } = new();

    public EventLogLauncher Launcher { get; } = new();

    public EventLogServices Services { get; }

    public FakeRemoteComputerClient Remote { get; }

    public FakeNameLookup Lookup { get; } = new();

    public GroupPolicyServices? GroupPolicy { get; }

    public List<GroupPolicyDocumentRequest> GroupPolicyRequests { get; } = [];

    public List<EventLogDocumentRequest> Requests { get; } = [];

    /// <summary>Values of dc02 for DNs (attribute → value), returned by its session instead of the directory's.</summary>
    public Dictionary<string, Dictionary<string, string>> Dc02Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SampleCompany Company => B2.Company;

    public FakeDirectorySession Session => B2.Session;

    public static async Task<EventLogHarness> CreateAsync(string culture = "pl-PL", bool withGroupPolicy = true, bool withRemote = true)
    {
        var harness = new EventLogHarness(await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture)), withGroupPolicy, withRemote);
        await harness.UseSampleSidAsync(harness.Company.JanKowalski, EventLogSamples.JanKowalski);
        return harness;
    }

    public static string FileTime(DateTimeOffset value) => AdConvert.ToFileTime(value).ToString(CultureInfo.InvariantCulture);

    /// <summary>The DN of a computer of the sample company by name.</summary>
    public string ComputerDn(string name) => $"CN={name},OU=Komputery,OU=Firma,{Session.Info.DefaultNamingContext}";

    /// <summary>Waits for a background task the view model started (fire and forget).</summary>
    public static Task WaitAsync(Func<bool> condition) => B2Harness.WaitUntilAsync(condition);

    /// <summary>Gives <paramref name="dn"/> the SID the samples log for <paramref name="account"/> (the object holding it gets a fresh one).</summary>
    private async Task UseSampleSidAsync(string dn, string account)
    {
        var sid = EventLogSamples.Sid(account);
        var search = new DirectorySearch
        {
            BaseDn = Session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = Core.Ldap.LdapFilter.Equal(Attr.ObjectSid, sid),
            Attributes = [Attr.Name],
        };
        var holders = new List<string>();
        await foreach (var holder in Session.SearchAsync(search, CancellationToken.None))
        {
            holders.Add(holder.DistinguishedName);
        }

        foreach (var holder in holders)
        {
            Session.SetValues(holder, Attr.ObjectSid, B2.Builder.NextSid());
        }

        Session.SetValues(dn, Attr.ObjectSid, sid);
    }

    private IReadOnlyDictionary<string, Dictionary<string, string>> DcValues(string server) =>
        string.Equals(server, Dc02, StringComparison.OrdinalIgnoreCase) ? Dc02Values : new Dictionary<string, Dictionary<string, string>>();
}

/// <summary>A reverse DNS double: address → name.</summary>
internal sealed class FakeNameLookup : IHostNameLookup
{
    public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

    public Task<string?> LookupAsync(IPAddress address, CancellationToken cancellationToken) =>
        Task.FromResult(Names.TryGetValue(address.ToString(), out var name) ? name : null);
}

/// <summary>A session of another DC whose searches return per-DC values (badPwdCount, lockoutTime … are not replicated).</summary>
internal sealed class DcValuesSession(IDirectorySession inner, IReadOnlyDictionary<string, Dictionary<string, string>> values) : IDirectorySession
{
    public SessionInfo Info => inner.Info;

    public ISchemaCatalog Schema => inner.Schema;

    public async IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var obj in inner.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            yield return values.TryGetValue(obj.DistinguishedName, out var extra)
                ? obj.With([.. extra.Select(e => new AttributeValues(e.Key, [e.Value]))])
                : obj;
        }
    }

    public Task<VirtualListPage> GetVirtualListPageAsync(DirectorySearch search, VirtualListRequest window, CancellationToken cancellationToken) =>
        inner.GetVirtualListPageAsync(search, window, cancellationToken);

    public Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken) =>
        inner.GetAsync(distinguishedName, attributes, cancellationToken);

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
