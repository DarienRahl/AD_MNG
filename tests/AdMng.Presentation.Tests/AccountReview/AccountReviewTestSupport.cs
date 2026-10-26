using System.Globalization;
using System.Runtime.CompilerServices;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.AccountReview;

/// <summary>
/// The sample company prepared for the account review (B2Harness: now = 2026-10-13 12:00 UTC, local time zone UTC):
/// constructed password expiry with a domain maximum age of 90 days and one fine-grained policy of 30 days, one account
/// in every state of the README table.
/// </summary>
internal sealed class ReviewScenario
{
    public const string PsoDn = "CN=PSO-IT-30,CN=Password Settings Container,CN=System,DC=contoso,DC=test";

    private ReviewScenario(B2Harness harness)
    {
        Harness = harness;
        var builder = harness.Builder;
        var session = harness.Session;
        var company = harness.Company;
        var now = harness.Time.GetUtcNow();
        session.EmulateConstructedPasswordExpiry = true;
        session.MaxPasswordAge = TimeSpan.FromDays(90);

        // Password: Jan expires in 10 days, Małgorzata expired 10 days ago, Łukasz expires in 5 days only through his PSO
        // (the domain's 90 days would give 65), Bartłomiej must change it.
        session.SetValues(company.JanKowalski, Attr.PwdLastSet, FileTime(now.AddDays(-80)));
        session.SetValues(company.MalgorzataWojcik, Attr.PwdLastSet, FileTime(now.AddDays(-100)));
        session.SetValues(company.LukaszWisniewski, Attr.PwdLastSet, FileTime(now.AddDays(-25)));
        session.Seed("CN=System,DC=contoso,DC=test", new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "container" } });
        session.Seed("CN=Password Settings Container,CN=System,DC=contoso,DC=test", new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "msDS-PasswordSettingsContainer" },
        });
        session.Seed(PsoDn, new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "msDS-PasswordSettings" },
            ["msDS-PasswordSettingsPrecedence"] = 10,
            ["msDS-MaximumPasswordAge"] = (-TimeSpan.FromDays(30).Ticks).ToString(CultureInfo.InvariantCulture),
        });
        session.SetResultantPso(company.LukaszWisniewski, PsoDn);
        session.SetValues(company.BartlomiejKozlowski, Attr.PwdLastSet, "0");

        // Paweł: the account ends with 16 October (3 days left) and his last logon is 120 days old.
        session.SetValues(company.PawelLewandowski, Attr.AccountExpires, FileTime(new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero)));
        session.SetValues(company.PawelLewandowski, Attr.LastLogonTimestamp, FileTime(now.AddDays(-120)));

        // Zbigniew (disabled, still in GG_Ksiegowosc) is still in the address book.
        session.SetValues(company.ZbigniewKazmierczak, Attr.ShowInAddressBook, "CN=Default Global Address List,CN=All Global Address Lists,CN=Address Lists Container,CN=Contoso,CN=Microsoft Exchange,CN=Services,CN=Configuration,DC=contoso,DC=test");

        Agnieszka = builder.AddUser("Agnieszka", "Żółć", company.SalesOu, new Dictionary<string, object?>
        {
            [Attr.AccountExpires] = FileTime(new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero)),
            [Attr.ProxyAddresses] = new[] { "smtp:a.zolc@contoso.test", "SMTP:agnieszka.zolc@contoso.test" },
            [Attr.Manager] = company.PawelLewandowski,
        });
        Wieslaw = builder.AddUser("Wiesław", "Pękala", company.AccountingOu, new Dictionary<string, object?>
        {
            [Attr.WhenCreated] = AdConvert.ToGeneralizedTime(now.AddDays(-200)),
            [Attr.Manager] = company.ZbigniewKazmierczak,
        });
        Tadeusz = builder.AddUser("Tadeusz", "Ślęzak", company.ItOu, new Dictionary<string, object?>
        {
            [Attr.UserAccountControl] = (int)(UserAccountControl.NormalAccount | UserAccountControl.PasswordNotRequired),
            [Attr.Mail] = "tadeusz.slezak@contoso.test",
            [Attr.Manager] = company.JanKowalski,
        });
        AdminsIt = builder.AddGroup("GG_Admini_IT", company.GroupsOu, members: [Tadeusz]);
        builder.AddMembers(builder.DomainAdminsDn, AdminsIt);
    }

    public B2Harness Harness { get; }

    public SampleCompany Company => Harness.Company;

    /// <summary>Expired two days ago; no mail but a primary SMTP proxy address.</summary>
    public string Agnieszka { get; }

    /// <summary>Created 200 days ago, never logged on, no mail, manager Zbigniew (disabled).</summary>
    public string Wieslaw { get; }

    /// <summary>PASSWD_NOTREQD and a nested member of Domain Admins (through GG_Admini_IT, no adminCount).</summary>
    public string Tadeusz { get; }

    public string AdminsIt { get; }

    public static async Task<ReviewScenario> CreateAsync(CultureInfo? culture = null) => new(await B2Harness.CreateAsync(culture));

    /// <summary>Connected through a session of dc01 (a reconnect closes only that session, not the shared directory).</summary>
    public static async Task<ReviewScenario> CreateOnDomainControllerAsync() => new(await B2Harness.CreateOnDomainControllerAsync());

    /// <summary>A factory whose reads of other DCs go through <see cref="B2Harness.ConnectToServer"/>.</summary>
    public DialogViewModelFactory FactoryWithConnector(IUserMessageLauncher? messages = null) => new(
        Harness.Services, Harness.Engines, Harness.Views, Harness.AuditLog, new AuditLogExporter(new CsvCodec()), Harness.PowerShell,
        Harness.Theme, connector: new ServerConnector(Harness), messages: messages);

    public static string FileTime(DateTimeOffset value) => AdConvert.ToFileTime(value).ToString(CultureInfo.InvariantCulture);

    /// <summary>A review window with all checks, run over the whole domain.</summary>
    public async Task<AccountReviewViewModel> RunAsync(Action<AccountReviewViewModel>? arrange = null)
    {
        var review = Harness.Factory.CreateAccountReview();
        await review.InitializeAsync();
        arrange?.Invoke(review);
        await review.RunCommand.ExecuteAsync(null);
        return review;
    }
}

internal static class ReviewAssert
{
    /// <summary>Selects the tile of <paramref name="check"/> and returns the names in its list.</summary>
    public static IReadOnlyList<string> Names(AccountReviewViewModel review, AccountCheck check)
    {
        review.SelectedTile = Assert.Single(review.Tiles, t => t.Check == check);
        return [.. review.Items.Select(r => r.Name).Order(StringComparer.Ordinal)];
    }

    public static AccountReviewRowViewModel Row(AccountReviewViewModel review, AccountCheck check, string name)
    {
        review.SelectedTile = Assert.Single(review.Tiles, t => t.Check == check);
        return Assert.Single(review.Items, r => r.Name == name);
    }
}

/// <summary>A session around another one whose searches can be changed (lastLogon of another DC) or held (stop).</summary>
internal sealed class HookedSession(IDirectorySession inner, string? server = null) : IDirectorySession
{
    /// <summary>Values to add to the objects a search returns (DN → attribute → value).</summary>
    public Dictionary<string, (string Attribute, string Value)> Extra { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>After this many objects of the review query (user filter with attributes) the search waits until cancelled.</summary>
    public int? HoldAfter { get; set; }

    /// <summary>Completed when a search started to wait (<see cref="HoldAfter"/>).</summary>
    public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsDisposed { get; private set; }

    public SessionInfo Info { get; } = server is null ? inner.Info : inner.Info with { Server = server };

    public ISchemaCatalog Schema => inner.Schema;

    public async IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var count = 0;
        await foreach (var obj in inner.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            if (HoldAfter is { } limit && count++ >= limit && search.Attributes.Count > 0 && search.Filter.Contains("805306368", StringComparison.Ordinal))
            {
                Held.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            yield return Extra.TryGetValue(obj.DistinguishedName, out var extra)
                ? obj.With([new AttributeValues(extra.Attribute, [extra.Value])])
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

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Opens sessions to other DCs through the harness (the review's "all DCs" reads).</summary>
internal sealed class ServerConnector(B2Harness harness) : IDirectoryConnector
{
    public string? GetComputerDomain() => FakeDirectoryBuilder.DefaultDomain;

    public Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DomainControllerInfo>>([]);

    public Task<IDirectorySession> ConnectAsync(ConnectionOptions options, System.Net.NetworkCredential? credential, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IDirectorySession> ConnectToServerAsync(IDirectorySession current, string server, CancellationToken cancellationToken) =>
        harness.ConnectToServer(current, server, cancellationToken);
}

/// <summary>U1 stand-in: records what the review hands over.</summary>
internal sealed class RecordingMessageLauncher : IUserMessageLauncher
{
    public List<(IReadOnlyList<string> Dns, AccountCheck? Reason)> Requests { get; } = [];

    public Task ShowComposerAsync(IReadOnlyList<string> distinguishedNames, AccountCheck? reason)
    {
        Requests.Add((distinguishedNames, reason));
        return Task.CompletedTask;
    }
}
