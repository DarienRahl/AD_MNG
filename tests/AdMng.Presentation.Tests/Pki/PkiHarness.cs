using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Session;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using AdMng.Testing.Pki;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// The PKI services of one connection window over the fake directory with the Polish PKI sample
/// (<see cref="FakeDirectoryBuilder.AddPublicKeySample"/>, docs/dev/p-certificate-services.md 2.10), copied from
/// <c>GroupPolicyHarness</c>: the real Core readers through <see cref="DirectPkiSnapshotService"/> (no caching – W6/W7 tests
/// do not need W5's <c>PkiSnapshotService</c>), the TLS and HTTP fakes, the window's UI services of <see cref="B2Harness"/>
/// (pl-PL by default, the fake clock 2026-10-13 12:00).
/// </summary>
internal sealed class PkiHarness
{
    private PkiHarness(B2Harness b2)
    {
        B2 = b2;
        Sample = b2.Builder.AddPublicKeySample(b2.Company);
        Snapshot = new DirectPkiSnapshotService(b2.Services, TlsProbe, HealthProbe);
        Services = new PkiServices(b2.Services, new CsvCodec(), Snapshot, TlsProbe, HealthProbe);
        Factory = new PkiViewModelFactory(Services);
    }

    public B2Harness B2 { get; }

    public PublicKeySample Sample { get; }

    public FakeDomainControllerTlsProbe TlsProbe { get; } = new();

    public FakePkiHealthProbe HealthProbe { get; } = new();

    public DirectPkiSnapshotService Snapshot { get; }

    public PkiServices Services { get; }

    public PkiViewModelFactory Factory { get; }

    public FakeDirectorySession Session => B2.Session;

    public SampleCompany Company => B2.Company;

    public FakeDirectoryBuilder Builder => B2.Builder;

    /// <param name="culture">UI language (default pl-PL).</param>
    public static async Task<PkiHarness> CreateAsync(CultureInfo? culture = null) =>
        new(await B2Harness.CreateAsync(culture ?? CultureInfo.GetCultureInfo("pl-PL")));

    /// <summary>The DN of a template of the sample by its cn (<see cref="PublicKeyTemplates"/>).</summary>
    public string TemplateDn(string cn) => Sample.Template(cn);

    /// <summary>A loaded document of the request.</summary>
    public async Task<T> OpenAsync<T>(PkiDocumentRequest request)
        where T : PkiDocumentBase
    {
        var document = (T)Factory.Create(request);
        await document.LoadAsync();
        return document;
    }

    /// <summary>The main window's shell over the same services (its tree has the window's <see cref="PkiViewModelFactory"/>).</summary>
    public ShellViewModel CreateShell() => new(
        B2.Lists,
        new NavigationTreeViewModel(B2.Sessions, B2.Settings, B2.Localizer, B2.Dispatcher, B2.Errors, B2.Views, B2.Changes, pki: Factory),
        B2.Activities,
        B2.Coordinator,
        B2.Views,
        B2.Dialogs,
        B2.Files,
        B2.Clipboard,
        B2.Notifications,
        new CsvCodec(),
        B2.Regional);
}

/// <summary>
/// An <see cref="IPkiSnapshotService"/> that runs the Core readers on every call (no cache, no shared builds): the snapshot
/// (<see cref="PkiOverviewReader.ReadAsync"/>), the expansion (<c>GroupGraph.ReadAsync</c>, <see cref="AclObjectTypes.ReadAsync"/>
/// and <see cref="TemplateRightsEvaluator.ExpandAsync"/>), the DCs, their LDAPS checks on port 636 through the probe and the
/// HTTP check of <see cref="PkiHealth.HttpUrls"/>. The last results stay readable through the properties; <see cref="Changed"/>
/// fires after each call. The test double of W6/W7 until W5's <c>PkiSnapshotService</c> is merged.
/// </summary>
internal sealed class DirectPkiSnapshotService(DialogServices ui, IDomainControllerTlsProbe? tlsProbe, IPkiHealthProbe? healthProbe) : IPkiSnapshotService
{
    /// <summary>How many times the snapshot was read.</summary>
    public int Reads { get; private set; }

    public PkiSnapshot? Current { get; private set; }

    public DateTimeOffset? ReadAt => Current?.ReadAt;

    public bool IsBuilding { get; private set; }

    public PkiEnrollmentExpansion? Expansion { get; private set; }

    public IReadOnlyList<DcTlsCheck>? DcCertificates { get; private set; }

    public IReadOnlyList<PkiHttpCheckResult>? HttpResults { get; private set; }

    public event EventHandler? Changed;

    public async Task<PkiSnapshot> GetAsync(bool refresh, IProgress<PkiReadProgress>? progress, CancellationToken cancellationToken)
    {
        var session = Session();
        IsBuilding = true;
        try
        {
            Current = await PkiOverviewReader.ReadAsync(session, new PkiReadOptions { Time = ui.Time }, progress, cancellationToken);
            Reads++;
        }
        finally
        {
            IsBuilding = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Current;
    }

    public async Task<PkiEnrollmentExpansion> GetExpansionAsync(bool refresh, IProgress<PkiExpansionProgress>? progress, CancellationToken cancellationToken)
    {
        var session = Session();
        var snapshot = Current ?? await GetAsync(false, null, cancellationToken);
        var domainSid = (await session.GetAsync(session.Info.DefaultNamingContext, [Attr.ObjectSid], cancellationToken))?.ObjectSid;
        var options = new PrivilegedAccessOptions { DomainSid = domainSid, RootDomainSid = domainSid };
        var groups = await GroupGraph.ReadAsync(session, null, options.PageSize, cancellationToken);
        var types = await AclObjectTypes.ReadAsync(session, options.PageSize, cancellationToken);
        var classifier = new PrincipalClassifier(domainSid, domainSid, options, groups, [.. snapshot.EnterpriseCas.Select(c => c.ComputerSid).OfType<string>()]);
        Expansion = await TemplateRightsEvaluator.ExpandAsync(session, snapshot.Templates, new PkiExpansionContext(groups, types, classifier, options), progress, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return Expansion;
    }

    public Task<IReadOnlyList<PkiDomainController>> GetDomainControllersAsync(bool refresh, CancellationToken cancellationToken) =>
        PkiOverviewReader.ReadDomainControllersAsync(Session(), cancellationToken);

    public async Task<IReadOnlyList<DcTlsCheck>> GetDcCertificatesAsync(bool refresh, CancellationToken cancellationToken)
    {
        var session = Session();
        var snapshot = Current ?? await GetAsync(false, null, cancellationToken);
        var checks = new List<DcTlsCheck>();
        if (tlsProbe is not null)
        {
            foreach (var dc in await GetDomainControllersAsync(refresh, cancellationToken))
            {
                if (dc.HostName is not { } host)
                {
                    continue;
                }

                var result = await tlsProbe.ProbeAsync(host, 636, TimeSpan.FromSeconds(5), cancellationToken);
                var certificate = result.Outcome == TlsProbeOutcome.Ok
                    ? DcCertificateEvaluation.Evaluate(result.PresentedChain, host, session.Info.DomainDnsName, snapshot.TrustMaterial, ui.Time.GetUtcNow())
                    : null;
                checks.Add(new DcTlsCheck(host, 636, result.Outcome, certificate));
            }
        }

        DcCertificates = checks;
        Changed?.Invoke(this, EventArgs.Empty);
        return checks;
    }

    public async Task<IReadOnlyList<PkiHttpCheckResult>> CheckHttpAsync(IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var snapshot = Current ?? await GetAsync(false, null, cancellationToken);
        HttpResults = healthProbe is null ? [] : await healthProbe.CheckAsync(PkiHealth.HttpUrls(snapshot), progress, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return HttpResults;
    }

    public void Invalidate()
    {
        Current = null;
        Expansion = null;
        DcCertificates = null;
        HttpResults = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private IDirectorySession Session() => ui.Sessions.Current ?? throw new SessionEndedException();
}
