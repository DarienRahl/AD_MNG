using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// The Kestrel TLS certificate (spec 19.3): LocalMachine\My (configurable store) by <c>Thumbprint</c>, else by
/// <c>Subject</c> – the valid certificate with serverAuth EKU and a private key that expires last. Reloaded every
/// 10 minutes (renewals are picked up without a restart); <see cref="Current"/> is what the server certificate selector
/// returns.
/// </summary>
public sealed class CertificateSelector : IDisposable
{
    /// <summary>Reload interval.</summary>
    public static readonly TimeSpan ReloadInterval = TimeSpan.FromMinutes(10);

    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    private readonly ICertificateStore _store;
    private readonly IOptionsMonitor<ServiceOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private X509Certificate2? _current;
    private ITimer? _timer;

    public CertificateSelector(ICertificateStore store, IOptionsMonitor<ServiceOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>The selected certificate (selected on first use), or null when none matches.</summary>
    public X509Certificate2? Current
    {
        get
        {
            lock (_gate)
            {
                if (_current is null)
                {
                    _current = Find();
                }

                return _current;
            }
        }
    }

    /// <summary>Selects again now and starts the periodic reload.</summary>
    public X509Certificate2? Reload()
    {
        var found = Find();
        lock (_gate)
        {
            if (found is not null)
            {
                _current = found;
            }

            _timer ??= _timeProvider.CreateTimer(static state => ((CertificateSelector)state!).Reload(), this, ReloadInterval, ReloadInterval);
            return _current;
        }
    }

    /// <summary>The matching certificate of the configured store (no side effects).</summary>
    public X509Certificate2? Find()
    {
        var settings = _options.CurrentValue.Hosting.Certificate;
        var storeName = Enum.TryParse<StoreName>(settings.Store, ignoreCase: true, out var name) ? name : StoreName.My;
        var location = Enum.TryParse<StoreLocation>(settings.Location, ignoreCase: true, out var loc) ? loc : StoreLocation.LocalMachine;
        return Select(_store.GetCertificates(storeName, location), settings, _timeProvider.GetUtcNow());
    }

    /// <summary>The selection rule over <paramref name="certificates"/>.</summary>
    public static X509Certificate2? Select(IEnumerable<X509Certificate2> certificates, CertificateOptions settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(settings);
        var usable = certificates.Where(c => c.HasPrivateKey && c.NotBefore <= now.LocalDateTime && c.NotAfter > now.LocalDateTime);
        if (!string.IsNullOrWhiteSpace(settings.Thumbprint))
        {
            var thumbprint = new string([.. settings.Thumbprint.Where(char.IsAsciiHexDigit)]);
            return usable.FirstOrDefault(c => string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(settings.Subject))
        {
            return null;
        }

        var subject = settings.Subject.Trim();
        return usable
            .Where(c => HasServerAuth(c) && SubjectMatches(c, subject))
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();
    }

    /// <summary>The start-up self-check: signs test data with the private key (fails when the service account has no
    /// Read permission on the key).</summary>
    public static bool CanUsePrivateKey(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        byte[] data = [0x41, 0x64, 0x4d, 0x6e, 0x67];
        try
        {
            using var rsa = certificate.GetRSAPrivateKey();
            if (rsa is not null)
            {
                return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Length > 0;
            }

            using var ecdsa = certificate.GetECDsaPrivateKey();
            return ecdsa is not null && ecdsa.SignData(data, HashAlgorithmName.SHA256).Length > 0;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private static bool HasServerAuth(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(u => u.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ServerAuthOid));

    private static bool SubjectMatches(X509Certificate2 certificate, string subject) =>
        string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false), subject, StringComparison.OrdinalIgnoreCase)
        || string.Equals(certificate.Subject, subject, StringComparison.OrdinalIgnoreCase)
        || string.Equals(certificate.Subject, "CN=" + subject, StringComparison.OrdinalIgnoreCase);
}
