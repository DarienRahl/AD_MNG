using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Connections;
using AdMng.Testing;
using AdMng.Testing.Pki;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// The P1 contract commit (docs/dev/p-certificate-services.md 2.11) on the Presentation side: the records of
/// <c>AdMng.Presentation.Pki</c> print no directory value, the registrations of <c>AddAdMngPki()</c>, the complete
/// <see cref="PkiServices"/> and <see cref="PkiDocumentBase"/>, the settings, and a shell that behaves as before.
/// </summary>
public sealed class PkiContractTests
{
    public static TheoryData<string> RecordNames()
    {
        var data = new TheoryData<string>();
        foreach (var type in PkiRecordSamples.Records(typeof(PkiServices).Assembly, "AdMng.Presentation.Pki"))
        {
            data.Add(type.FullName!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RecordNames))]
    public void EveryRecord_DeclaresAValueFreeToString(string typeName)
    {
        var type = typeof(PkiServices).Assembly.GetType(typeName, throwOnError: true)!;

        Assert.True(PkiRecordSamples.DeclaresOwnToString(type), $"{type.Name} uses the compiler-generated ToString (it prints every member).");
        Assert.Equal([], PkiRecordSamples.LeakedValues(type));
    }

    [Fact]
    public void DocumentRequest_PrintsItsKindOnly()
    {
        var request = new PkiDocumentRequest(PkiDocumentKind.Templates) { TemplateCn = "Użytkownik-VPN-Podatny", CaDn = "CN=Kontoso Główny CA", FindingCode = "x" };

        Assert.Equal("PkiDocumentRequest(Templates)", request.ToString());
    }

    [Fact]
    public async Task AddAdMngPki_RegistersOneCachePerWindow_AndTheSharedHttpCheck()
    {
        await using var host = new ConnectionScopeHost(domains: [new FakeDirectoryBuilder().Build()]);
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();

        Assert.Same(a.Get<PkiSnapshotService>(), a.Get<IPkiSnapshotService>());
        Assert.Same(a.Get<IPkiSnapshotService>(), a.Get<PkiServices>().Snapshot);
        Assert.Same(a.Get<PkiServices>(), a.Get<PkiViewModelFactory>().Pki);
        Assert.NotSame(a.Get<PkiServices>(), b.Get<PkiServices>());
        Assert.IsType<PkiHealthProbe>(a.Get<IPkiHealthProbe>());
        Assert.Same(a.Get<IPkiHealthProbe>(), a.Get<PkiServices>().HealthProbe);
        Assert.Same(a.Get<IPkiHealthProbe>(), b.Get<IPkiHealthProbe>());

        // Presentation registers no TLS probe: without the App's SslStreamTlsProbe the LDAPS tab says "niedostępne".
        Assert.Null(host.Provider.GetService<IDomainControllerTlsProbe>());
        Assert.Null(a.Get<PkiServices>().TlsProbe);
        Assert.Null(a.Get<PkiSnapshotService>().Current);
    }

    [Fact]
    public async Task PkiServices_RequestsDocuments_AndOpensTheCaComputer()
    {
        var harness = await PkiHarness.CreateAsync();
        var pki = harness.Services;
        var requests = new List<PkiDocumentRequest>();

        Assert.False(pki.RequestDocument(new PkiDocumentRequest(PkiDocumentKind.Overview)));
        pki.DocumentRequested += (_, r) => requests.Add(r);
        Assert.True(pki.RequestDocument(new PkiDocumentRequest(PkiDocumentKind.SecurityReview) { FindingCode = "TemplateWriteable" }));
        Assert.Equal(PkiDocumentKind.SecurityReview, Assert.Single(requests).Kind);

        await pki.OpenComputerAsync(harness.Sample.IssuingCaComputer);
        Assert.Equal([harness.Sample.IssuingCaComputer], harness.B2.Dialogs.PropertiesShown);
        Assert.Same(harness.TlsProbe, pki.TlsProbe);
        Assert.Equal(CancellationToken.None, pki.Lifetime);
    }

    [Fact]
    public async Task DocumentBase_LoadsOnTheSession_ReloadsAndUnsubscribes()
    {
        var harness = await PkiHarness.CreateAsync();
        using var document = new ProbeDocument(harness.Services);

        await document.LoadAsync();
        Assert.Equal(1, document.Loads);
        Assert.False(document.LastWasRefresh);
        Assert.Equal("1", document.StatusText);

        await document.RefreshAsync();
        Assert.Equal(2, document.Loads);
        Assert.True(document.LastWasRefresh);

        await harness.Services.Settings.UpdateAsync(s => s.Pki.CertificateExpiryWarningDays = 45, TestContext.Current.CancellationToken);
        Assert.Equal(1, document.SettingsChanges);

        document.Fail = new DirectoryException(DirectoryErrorKind.ServerUnavailable, "down");
        await document.LoadAsync();
        Assert.True(document.HasError);
        Assert.False(document.IsLoading);

        document.Dispose();
        await harness.Services.Settings.UpdateAsync(s => s.Pki.CertificateExpiryWarningDays = 60, TestContext.Current.CancellationToken);
        Assert.Equal(1, document.SettingsChanges);
    }

    [Fact]
    public async Task DocumentBase_WithoutASession_SaysNotConnected()
    {
        var harness = await PkiHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"));
        await harness.B2.Sessions.DisconnectAsync();
        using var document = new ProbeDocument(harness.Services);

        await document.LoadAsync();

        Assert.Equal(0, document.Loads);
        Assert.Equal(harness.B2.Localizer[L.Errors.NotConnected], document.Error);
    }

    [Fact]
    public async Task DocumentBase_SessionEndingDuringALoad_StopsTheSpinner()
    {
        var harness = await PkiHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"));
        using var document = new ProbeDocument(harness.Services) { Gate = new TaskCompletionSource() };

        var load = document.RefreshAsync();
        Assert.True(document.IsLoading);
        Assert.True(document.Refreshing);

        // The session change starts a new load (cancelling the running one), which finds no session.
        await harness.B2.Sessions.DisconnectAsync();
        await load;

        Assert.False(document.IsLoading);
        Assert.False(document.Refreshing);
        Assert.False(document.HasSession);
        Assert.Equal(harness.B2.Localizer[L.Errors.NotConnected], document.Error);
        Assert.Equal(0, document.Loads);
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(System.Security.Cryptography.CryptographicException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(TimeoutException))]
    public async Task DocumentBase_TurnsParsingAndAccessFailures_IntoAnError(Type failure)
    {
        var harness = await PkiHarness.CreateAsync();
        using var document = new ProbeDocument(harness.Services) { Fail = (Exception)Activator.CreateInstance(failure)! };

        await document.LoadAsync();

        Assert.True(document.HasError);
        Assert.False(document.IsLoading);
    }

    [Fact]
    public void Settings_DefaultToThirtyAndHundredEightyDays_AndClamp()
    {
        var settings = new AppSettings();
        Assert.True(settings.Pki.ShowInTree);
        Assert.Equal(new Core.Pki.PkiHealthThresholds(30, 180), settings.Pki.Thresholds());

        settings.Pki.CertificateExpiryWarningDays = 0;
        settings.Pki.CaCertificateExpiryWarningDays = 99_999;
        settings.Normalize();
        Assert.Equal(new Core.Pki.PkiHealthThresholds(1, 3650), settings.Pki.Thresholds());

        var old = new AppSettings { Pki = null! };
        old.Normalize();
        Assert.NotNull(old.Pki);
    }

    [Fact]
    public async Task TheShell_ListsThePkiDescriptors_AndOpensNothingByItself()
    {
        var harness = await PkiHarness.CreateAsync();
        using var shell = harness.CreateShell();

        Assert.Equal(["pki.overview", "pki.templates", "pki.security", "pki.readiness"],
            shell.CommandDescriptors.Where(d => d.Id.StartsWith("pki.", StringComparison.Ordinal)).Select(d => d.Id));
        Assert.Empty(shell.PkiDocuments);
        Assert.Null(shell.ActivePki());
        Assert.True(Enum.IsDefined(ObjectIcon.Certificate));
    }

    /// <summary>The smallest PKI document: counts its loads, reports the settings changes, fails on request.</summary>
    private sealed class ProbeDocument(PkiServices pki) : PkiDocumentBase(pki)
    {
        public int Loads { get; private set; }

        public bool LastWasRefresh { get; private set; }

        public int SettingsChanges { get; private set; }

        public Exception? Fail { get; set; }

        /// <summary>Holds the load until set (or cancelled).</summary>
        public TaskCompletionSource? Gate { get; init; }

        public bool Refreshing => IsRefresh;

        public override string Title => "Probe";

        public override ObjectIcon Icon => ObjectIcon.Certificate;

        protected override async Task LoadCoreAsync(IDirectorySession session, CancellationToken cancellationToken)
        {
            if (Fail is { } failure)
            {
                throw failure;
            }

            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            Loads++;
            LastWasRefresh = IsRefresh;
            LoadedText = Loads.ToString(CultureInfo.InvariantCulture);
        }

        protected override void OnSettingsChanged() => SettingsChanges++;
    }
}
