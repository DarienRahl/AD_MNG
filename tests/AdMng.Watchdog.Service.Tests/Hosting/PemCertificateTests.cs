using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>The PEM certificate of the Linux service (stage B design 6.3): loading, the key/certificate pair check, reload
/// and file-change reload, the fingerprint.</summary>
public sealed class PemCertificateTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private readonly TestDataDirectory _directory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CertificatePath => _directory.File("admng-wd.crt");

    private string KeyPath => _directory.File("admng-wd.key");

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Load_ReturnsTheCertificateWithItsKey()
    {
        var thumbprint = WritePair("admng-wd.contoso.com");

        var loaded = PemCertificateSource.Load(CertificatePath, KeyPath);

        Assert.Null(loaded.Problem);
        Assert.Equal(thumbprint, loaded.Certificate?.Thumbprint);
        Assert.True(loaded.Certificate!.HasPrivateKey);
        Assert.True(CertificateSelector.CanUsePrivateKey(loaded.Certificate));
    }

    [Fact]
    public void Load_RecognisesAKeyOfAnotherCertificate_AndMissingOrInvalidFiles()
    {
        WritePair("admng-wd.contoso.com");
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(KeyPath, otherKey.ExportPkcs8PrivateKeyPem());

        Assert.Equal(PemCertificateSource.KeyMismatch, PemCertificateSource.Load(CertificatePath, KeyPath).Problem);
        Assert.Equal(PemCertificateSource.Missing, PemCertificateSource.Load(_directory.File("none.crt"), KeyPath).Problem);
        File.WriteAllText(KeyPath, "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n");
        Assert.Equal(PemCertificateSource.Invalid, PemCertificateSource.Load(CertificatePath, KeyPath).Problem);
    }

    [Fact]
    public void Fingerprint_LooksLikeOpenSsl()
    {
        WritePair("admng-wd.contoso.com");
        using var certificate = PemCertificateSource.Load(CertificatePath, KeyPath).Certificate!;

        var fingerprint = PemCertificateSource.Fingerprint(certificate);

        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", fingerprint);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(certificate.RawData)), fingerprint.Replace(":", string.Empty, StringComparison.Ordinal));
    }

    /// <summary>Renewal: a key overwritten before the new certificate arrives must not be applied – the current certificate
    /// keeps serving with health Error <c>certificate.key</c>; the matching pair is adopted at the next reload.</summary>
    [Fact]
    public void Reload_KeepsTheCurrentCertificate_WhileTheKeyDoesNotMatch()
    {
        var original = WritePair("admng-wd.contoso.com");
        var clock = new FakeTimeProvider(Now);
        var health = new FakeHealthRegistry();
        var log = new TestLog();
        using var selector = new CertificateSelector(new NoStore(), PemOptions(), clock, health, log.For<CertificateSelector>());
        Assert.Equal(original, selector.Reload()?.Thumbprint);

        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(KeyPath, newKey.ExportPkcs8PrivateKeyPem());
        clock.Advance(CertificateSelector.ReloadInterval);

        Assert.Equal(original, selector.Current?.Thumbprint);
        var problem = Assert.Single(health.Checks, c => c.Code == CertificateSelector.KeyHealthCode);
        Assert.Equal(HealthState.Error, problem.State);
        Assert.Equal([PemCertificateSource.KeyMismatch], problem.Args);
        clock.Advance(CertificateSelector.ReloadInterval);
        Assert.Single(log.Entries, e => e.EventId.Id == EventLogIds.RuntimeOrCertificate && e.Message.Contains(PemCertificateSource.KeyMismatch, StringComparison.Ordinal));

        var renewed = WritePair("admng-wd.contoso.com", newKey);
        clock.Advance(CertificateSelector.ReloadInterval);

        Assert.Equal(renewed, selector.Current?.Thumbprint);
        Assert.Equal(HealthState.Ok, Assert.Single(health.Checks, c => c.Code == CertificateSelector.KeyHealthCode).State);
    }

    [Fact]
    public void ExpiredOrClientOnlyPem_IsNotSelected()
    {
        WritePair("admng-wd.contoso.com", notAfter: Now.AddHours(-1));
        using var expired = new CertificateSelector(new NoStore(), PemOptions(), new FakeTimeProvider(Now));
        Assert.Null(expired.Reload());

        WritePair("admng-wd.contoso.com", serverAuth: false);
        using var clientOnly = new CertificateSelector(new NoStore(), PemOptions(), new FakeTimeProvider(Now));
        Assert.Null(clientOnly.Reload());
    }

    [Fact]
    public async Task ChangedFiles_AreReloaded_WithoutWaitingForTheInterval()
    {
        var original = WritePair("admng-wd.contoso.com");
        using var selector = new CertificateSelector(new NoStore(), PemOptions(), TimeProvider.System);
        Assert.Equal(original, selector.Reload()?.Thumbprint);

        var renewed = WritePair("admng-wd.contoso.com");
        for (var i = 0; i < 150 && selector.Current?.Thumbprint != renewed; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }

        Assert.Equal(renewed, selector.Current?.Thumbprint);
    }

    [Fact]
    public void Validator_AcceptsAPemCertificate_ForHttps()
    {
        var options = new ServiceOptions
        {
            Hosting = new HostingOptions
            {
                Urls = "https://admng-wd.contoso.com:8443",
                Certificate = new CertificateOptions { Path = CertificateOptions.DefaultLinuxPath, KeyPath = CertificateOptions.DefaultLinuxKeyPath, Subject = null },
            },
        };

        Assert.DoesNotContain(ServiceOptionsValidator.Validate(options, "Production"), i => i.Path == "Watchdog:Hosting:Certificate");
    }

    private StaticOptionsMonitor<ServiceOptions> PemOptions() => new(new ServiceOptions
    {
        Hosting = new HostingOptions { Certificate = new CertificateOptions { Path = CertificatePath, KeyPath = KeyPath } },
    });

    /// <summary>Writes a self-signed ECDSA certificate and its key (renewal style: key first, then certificate) and returns
    /// the thumbprint.</summary>
    private string WritePair(string name, ECDsa? key = null, DateTimeOffset? notAfter = null, bool serverAuth = true)
    {
        using var created = key is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var signer = key ?? created!;
        var request = new CertificateRequest("CN=" + name, signer, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(serverAuth ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], critical: false));
        var subjectAlternativeName = new SubjectAlternativeNameBuilder();
        subjectAlternativeName.AddDnsName(name);
        request.CertificateExtensions.Add(subjectAlternativeName.Build());
        using var certificate = request.CreateSelfSigned(Now.AddDays(-1), notAfter ?? Now.AddDays(825));
        File.WriteAllText(KeyPath, signer.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(CertificatePath, certificate.ExportCertificatePem());
        return certificate.Thumbprint;
    }

    private sealed class NoStore : ICertificateStore
    {
        public IReadOnlyList<X509Certificate2> GetCertificates(StoreName storeName, StoreLocation location) => [];
    }
}
