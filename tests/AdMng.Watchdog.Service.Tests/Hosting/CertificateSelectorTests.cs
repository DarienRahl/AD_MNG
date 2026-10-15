using System.Security.Cryptography.X509Certificates;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Testing.Certificates;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>TLS certificate selection over an in-memory store (spec 19.3).</summary>
public sealed class CertificateSelectorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Subject_PicksTheValidServerCertificate_ThatExpiresLast()
    {
        var older = TestCertificates.Create("admng-wd.contoso.com", Now.AddDays(-30), Now.AddDays(20));
        var newer = TestCertificates.Create("admng-wd.contoso.com", Now.AddDays(-1), Now.AddDays(300));
        var expired = TestCertificates.Create("admng-wd.contoso.com", Now.AddDays(-400), Now.AddDays(-1));
        var clientOnly = TestCertificates.Create("admng-wd.contoso.com", Now.AddDays(-1), Now.AddDays(900), serverAuth: false);
        var other = TestCertificates.Create("inny.contoso.test", Now.AddDays(-1), Now.AddDays(999));

        var selected = CertificateSelector.Select([older, expired, clientOnly, other, newer], new CertificateOptions { Subject = "admng-wd.contoso.com" }, Now);

        Assert.Equal(newer.Thumbprint, selected?.Thumbprint);
    }

    [Fact]
    public void Thumbprint_WinsOverSubject_AndIgnoresSpacesAndCase()
    {
        var first = TestCertificates.Create("admng-wd.contoso.com");
        var second = TestCertificates.Create("admng-wd.contoso.com", notAfter: Now.AddDays(600));
        var thumbprint = string.Join(' ', first.Thumbprint.ToLowerInvariant().Chunk(2).Select(c => new string(c)));

        var selected = CertificateSelector.Select([second, first], new CertificateOptions { Thumbprint = thumbprint, Subject = "admng-wd.contoso.com" }, Now);

        Assert.Equal(first.Thumbprint, selected?.Thumbprint);
    }

    [Fact]
    public void CertificatesWithoutAPrivateKey_AreNeverSelected()
    {
        using var publicOnly = X509CertificateLoader.LoadCertificate(TestCertificates.Localhost.RawData);

        Assert.Null(CertificateSelector.Select([publicOnly], new CertificateOptions { Subject = "localhost" }, Now));
        Assert.Null(CertificateSelector.Select([TestCertificates.Localhost], new CertificateOptions(), Now));
    }

    [Fact]
    public void PrivateKeyCheck_SignsWithTheKey()
    {
        Assert.True(CertificateSelector.CanUsePrivateKey(TestCertificates.Localhost));
        using var publicOnly = X509CertificateLoader.LoadCertificate(TestCertificates.Localhost.RawData);
        Assert.False(CertificateSelector.CanUsePrivateKey(publicOnly));
    }

    [Fact]
    public void Reload_PicksUpARenewedCertificate_EveryTenMinutes()
    {
        var store = new InMemoryCertificateStore();
        var original = TestCertificates.Create("admng-wd.contoso.com", Now.AddDays(-1), Now.AddDays(10));
        store.Add(StoreName.My, original);
        var clock = new FakeTimeProvider(Now);
        var options = new StaticOptionsMonitor<ServiceOptions>(new ServiceOptions
        {
            Hosting = new HostingOptions { Certificate = new CertificateOptions { Subject = "admng-wd.contoso.com" } },
        });
        using var selector = new CertificateSelector(store, options, clock);

        Assert.Equal(original.Thumbprint, selector.Reload()?.Thumbprint);
        var renewed = TestCertificates.Create("admng-wd.contoso.com", Now.AddDays(-1), Now.AddDays(365));
        store.Add(StoreName.My, renewed);
        Assert.Equal(original.Thumbprint, selector.Current?.Thumbprint);

        clock.Advance(CertificateSelector.ReloadInterval);

        Assert.Equal(renewed.Thumbprint, selector.Current?.Thumbprint);
    }

    /// <summary>AD CS auto-enrollment renews the certificate, but the new key gets the default ACL: the service keeps the
    /// previous (still valid) certificate, reports it once – and switches when the key becomes readable.</summary>
    [Fact]
    public void Reload_KeepsTheCurrentCertificate_WhileTheRenewedKeyIsUnreadable()
    {
        var store = new InMemoryCertificateStore();
        var original = TestCertificates.Create("admng-wd.contoso.com", Now.AddDays(-300), Now.AddDays(45));
        store.Add(StoreName.My, original);
        var clock = new FakeTimeProvider(Now);
        var health = new FakeHealthRegistry();
        var log = new TestLog();
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var selector = new CertificateSelector(store, SubjectOptions(), clock, health, log.For<CertificateSelector>(), c => !unreadable.Contains(c.Thumbprint));
        Assert.Equal(original.Thumbprint, selector.Reload()?.Thumbprint);

        var renewed = TestCertificates.Create("admng-wd.contoso.com", Now.AddDays(-1), Now.AddDays(365));
        unreadable.Add(renewed.Thumbprint);
        store.Add(StoreName.My, renewed);
        clock.Advance(CertificateSelector.ReloadInterval);
        clock.Advance(CertificateSelector.ReloadInterval);

        Assert.Equal(original.Thumbprint, selector.Current?.Thumbprint);
        var problem = Assert.Single(health.Checks, c => c.Code == CertificateSelector.KeyHealthCode);
        Assert.Equal(HealthState.Warning, problem.State);
        Assert.Equal([renewed.Thumbprint], problem.Args);
        Assert.Single(log.Entries, e => e.EventId.Id == EventLogIds.RuntimeOrCertificate && e.Values.Contains(renewed.Thumbprint, StringComparison.OrdinalIgnoreCase));

        unreadable.Clear();   // the administrator grants the gMSA Read on the new key
        clock.Advance(CertificateSelector.ReloadInterval);

        Assert.Equal(renewed.Thumbprint, selector.Current?.Thumbprint);
        Assert.Equal(HealthState.Ok, Assert.Single(health.Checks, c => c.Code == CertificateSelector.KeyHealthCode).State);
    }

    [Fact]
    public void Reload_WithoutAUsableAlternative_ReportsAnError()
    {
        var store = new InMemoryCertificateStore();
        var original = TestCertificates.Create("admng-wd.contoso.com", Now.AddDays(-300), Now.AddMinutes(15));
        store.Add(StoreName.My, original);
        var clock = new FakeTimeProvider(Now);
        var health = new FakeHealthRegistry();
        var renewed = TestCertificates.Create("admng-wd.contoso.com", Now.AddDays(-1), Now.AddDays(365));
        using var selector = new CertificateSelector(store, SubjectOptions(), clock, health, new TestLog().For<CertificateSelector>(), c => c.Thumbprint != renewed.Thumbprint);
        selector.Reload();
        store.Add(StoreName.My, renewed);

        clock.Advance(TimeSpan.FromMinutes(20));   // the previous certificate has expired meanwhile

        Assert.Equal(HealthState.Error, Assert.Single(health.Checks, c => c.Code == CertificateSelector.KeyHealthCode).State);
    }

    [Fact]
    public void Reload_ReEvaluatesTheExpiryWarning()
    {
        var store = new InMemoryCertificateStore();
        var certificate = TestCertificates.Create("admng-wd.contoso.com", Now.AddDays(-300), Now.AddDays(30).AddHours(1));
        store.Add(StoreName.My, certificate);
        var clock = new FakeTimeProvider(Now);
        var health = new FakeHealthRegistry();
        var log = new TestLog();
        using var selector = new CertificateSelector(store, SubjectOptions(), clock, health, log.For<CertificateSelector>(), _ => true);
        selector.Reload();
        Assert.DoesNotContain(health.Checks, c => c.Code == CertificateSelector.ExpiryHealthCode);

        clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal(HealthState.Warning, Assert.Single(health.Checks, c => c.Code == CertificateSelector.ExpiryHealthCode).State);
        Assert.Single(log.Entries, e => e.EventId.Id == EventLogIds.RuntimeOrCertificate);
    }

    private static StaticOptionsMonitor<ServiceOptions> SubjectOptions() => new(new ServiceOptions
    {
        Hosting = new HostingOptions { Certificate = new CertificateOptions { Subject = "admng-wd.contoso.com" } },
    });

    private sealed class InMemoryCertificateStore : ICertificateStore
    {
        private readonly Lock _gate = new();
        private readonly List<(StoreName Store, X509Certificate2 Certificate)> _certificates = [];

        public void Add(StoreName store, X509Certificate2 certificate)
        {
            lock (_gate)
            {
                _certificates.Add((store, certificate));
            }
        }

        public IReadOnlyList<X509Certificate2> GetCertificates(StoreName storeName, StoreLocation location)
        {
            lock (_gate)
            {
                return [.. _certificates.Where(c => c.Store == storeName).Select(c => c.Certificate)];
            }
        }
    }
}
