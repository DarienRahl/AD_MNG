using System.Globalization;
using System.Text;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy.Cleanup;
using AdMng.Presentation.GroupPolicy.Copy;
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using Microsoft.Extensions.Logging;

namespace AdMng.Presentation.Tests.GroupPolicy.Copy;

/// <summary>
/// K7 W5 tests (docs/dev/k-computers-and-gpo.md 21.10): one Polish connection window over the GPO management sample plus
/// the cleanup sample (<see cref="FakeDirectoryBuilder.AddGroupPolicyCleanupSample"/>), the sample ADMX templates in the
/// central store (optional), the window's index service and the scripted Group Policy windows.
/// </summary>
internal sealed class K7Harness
{
    public static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private K7Harness(GroupPolicyHarness h)
    {
        H = h;
        Cleanup = h.B2.Builder.AddGroupPolicyCleanupSample(h.Company, h.Sysvol);
    }

    public GroupPolicyHarness H { get; }

    public GroupPolicyCleanupSample Cleanup { get; }

    public IGpoSettingsIndexService Index => H.Factory.Index;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="centralStore">Put the sample templates into the central store.</param>
    /// <param name="arrange">Runs before the first document is made.</param>
    /// <param name="culture">UI language (default Polish).</param>
    /// <param name="statusLog">Where the status lines of the documents write their log copies (default: nowhere).</param>
    public static async Task<K7Harness> CreateAsync(bool centralStore = true, Action<K7Harness>? arrange = null, CultureInfo? culture = null,
        ILogger<PageStatusHolder>? statusLog = null)
    {
        var inner = statusLog is null ? await GroupPolicyHarness.CreateAsync(culture ?? Polish) : await GroupPolicyHarness.CreateAsync(culture ?? Polish, statusLog);
        var harness = new K7Harness(inner);
        if (centralStore)
        {
            harness.H.AddCentralStore();
        }

        arrange?.Invoke(harness);
        return harness;
    }

    /// <summary>
    /// The harness connected to a domain controller session over the in-memory directory (<see cref="B2Harness.CreateOnDomainControllerAsync"/>),
    /// so that "Zmień kontroler domeny" can close it without closing the directory.
    /// </summary>
    public static async Task<K7Harness> CreateOnDomainControllerAsync()
    {
        var b2 = await B2Harness.CreateOnDomainControllerAsync("dc01.contoso.test", Polish);
        var gp = (GroupPolicyHarness)Activator.CreateInstance(typeof(GroupPolicyHarness), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [b2, true], CultureInfo.InvariantCulture)!;
        return new K7Harness(gp);
    }

    public Task<GpoIndexView> IndexAsync(GpoIndexRefresh refresh = GpoIndexRefresh.IfMissing) => Index.GetAsync(refresh, Ct);

    public Task<GpoCleanupDocumentViewModel> CleanupAsync() => H.OpenAsync<GpoCleanupDocumentViewModel>(GpoCleanupEntry.Request);

    /// <summary>The display items of the units of one GPO matching <paramref name="predicate"/> (index read when missing).</summary>
    public async Task<IReadOnlyList<GpoCopyItem>> ItemsAsync(Guid gpo, Func<GpoSettingUnit, bool>? predicate = null)
    {
        var view = await IndexAsync();
        var entry = view.Index.Find(gpo) ?? throw new InvalidOperationException("The GPO is not in the index.");
        var texts = GpoIndexTexts.For(H.B2.Localizer, view);
        return [.. entry.Units.Where(u => u.CountsAsSetting && (predicate?.Invoke(u) ?? true)).Select(u => GpoCopyItems.Of(texts, entry, u))];
    }

    public async Task<GpoInfo> GpoAsync(Guid id) => (await IndexAsync()).Index.Find(id)!.Gpo;

    /// <summary>A local user preference with a GPP password (MS14-025) in the machine half of <paramref name="gpo"/>.</summary>
    public void PutGppPassword(Guid gpo) => H.Sysvol.Put(gpo, "Machine/Preferences/Groups/Groups.xml", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Groups clsid=\"{3125E937-EB16-4b4c-9934-544FC6D24D26}\">"
        + "<User clsid=\"{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}\" name=\"serwis_łódź\" image=\"2\" changed=\"2026-10-20 10:00:00\" uid=\"{7A1B2C3D-4E5F-4061-8273-94A5B6C7D8E9}\">"
        + "<Properties action=\"U\" newName=\"\" fullName=\"Serwis Łódź\" description=\"\" cpassword=\"ZmFrZS1uaWUtaGFzbG8tdGVzdG93ZQ\" changeLogon=\"0\" noChange=\"0\" neverExpires=\"1\" acctDisabled=\"0\" userName=\"serwis_łódź\"/>"
        + "</User></Groups>")]);
}
