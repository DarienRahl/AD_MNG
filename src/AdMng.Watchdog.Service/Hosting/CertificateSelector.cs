using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// The Kestrel TLS certificate (spec 19.3): PEM files when <c>Path</c> is set (Linux, stage B design 6.3 –
/// <see cref="PemCertificateSource"/>, reloaded also on file change), else LocalMachine\My (configurable store) by
/// <c>Thumbprint</c>, else by <c>Subject</c> – the valid certificate with serverAuth EKU and a private key that expires
/// last. Reloaded every 10 minutes (renewals are picked up without a restart); <see cref="Current"/> is what the server
/// certificate selector returns. A PEM key that does not belong to its certificate is never applied: the current
/// certificate keeps serving and health <see cref="KeyHealthCode"/> is an Error until a reload finds a matching pair. A newly found certificate is adopted only when the service account can sign with its private key (an AD CS
/// renewal gives the new key the default ACL): otherwise the previous certificate stays while it is valid, and health
/// <see cref="KeyHealthCode"/> plus Application-log event 1007 (once per thumbprint while the previous certificate serves,
/// once more as an Error when none usable remains and handshakes fail) tell the administrator. Every
/// reload also re-evaluates the 30-day expiry warning (<see cref="ExpiryHealthCode"/>).
/// </summary>
public sealed partial class CertificateSelector : IDisposable
{
    /// <summary>Reload interval.</summary>
    public static readonly TimeSpan ReloadInterval = TimeSpan.FromMinutes(10);

    /// <summary>The expiry warning threshold.</summary>
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(30);

    /// <summary>Health check: a selected certificate whose private key the service account cannot use.</summary>
    public const string KeyHealthCode = "certificate.key";

    /// <summary>Health check: the certificate in use expires within <see cref="ExpiryWarning"/>.</summary>
    public const string ExpiryHealthCode = "certificate.expiry";

    /// <summary>Delay between a change of the PEM files and the reload (a renewal renames two files).</summary>
    public static readonly TimeSpan FileChangeDelay = TimeSpan.FromSeconds(2);

    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    private readonly ICertificateStore _store;
    private readonly IOptionsMonitor<ServiceOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly IHealthRegistry? _health;
    private readonly ILogger _logger;
    private readonly Func<X509Certificate2, bool> _canUsePrivateKey;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _loggedKeyProblems = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedExpiry = new(StringComparer.OrdinalIgnoreCase);
    private X509Certificate2? _current;
    private ITimer? _timer;
    private bool _keyProblemReported;
    private bool _currentKeyUnusable;
    private bool _expiryReported;
    private string? _pemProblem;
    private FileSystemWatcher? _watcher;
    private ITimer? _fileChangeTimer;

    public CertificateSelector(ICertificateStore store, IOptionsMonitor<ServiceOptions> options, TimeProvider timeProvider)
        : this(store, options, timeProvider, null, null)
    {
    }

    public CertificateSelector(
        ICertificateStore store,
        IOptionsMonitor<ServiceOptions> options,
        TimeProvider timeProvider,
        IHealthRegistry? health,
        ILogger<CertificateSelector>? logger)
        : this(store, options, timeProvider, health, logger, CanUsePrivateKey)
    {
    }

    /// <summary>With an explicit private-key check (tests).</summary>
    internal CertificateSelector(
        ICertificateStore store,
        IOptionsMonitor<ServiceOptions> options,
        TimeProvider timeProvider,
        IHealthRegistry? health,
        ILogger<CertificateSelector>? logger,
        Func<X509Certificate2, bool> canUsePrivateKey)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(canUsePrivateKey);
        _store = store;
        _options = options;
        _timeProvider = timeProvider;
        _health = health;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _canUsePrivateKey = canUsePrivateKey;
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

    /// <summary>Selects again now (see the class remarks) and starts the periodic reload.</summary>
    public X509Certificate2? Reload()
    {
        var settings = _options.CurrentValue.Hosting.Certificate;
        var now = _timeProvider.GetUtcNow();
        X509Certificate2? found;
        string? pemProblem = null;
        if (IsPem(settings))
        {
            (found, pemProblem) = FindPem(settings, now);
        }
        else
        {
            found = Find();
        }

        lock (_gate)
        {
            if (IsPem(settings))
            {
                WatchPemFiles(settings);
                ReportPemProblem(pemProblem);
            }

            var current = _current;
            var isCurrent = found is not null && string.Equals(found.Thumbprint, current?.Thumbprint, StringComparison.OrdinalIgnoreCase);
            if (found is not null && (!isCurrent || _currentKeyUnusable))
            {
                if (_canUsePrivateKey(found))
                {
                    _current = found;
                    _currentKeyUnusable = false;
                    ClearKeyProblem();
                }
                else
                {
                    var keepPrevious = !isCurrent && !_currentKeyUnusable && current is not null && IsValid(current, now);
                    ReportKeyProblem(found, keepPrevious);
                    if (!keepPrevious)
                    {
                        _current = found;   // nothing better: once the key ACL is fixed, the next handshake works
                        _currentKeyUnusable = true;
                    }
                }
            }

            if (IsPem(settings) && found is not null && !ReferenceEquals(found, _current))
            {
                found.Dispose();   // a fresh copy of the certificate in use (or one that was not applied)
            }

            CheckExpiry(_current, now);
            _timer ??= _timeProvider.CreateTimer(static state => ((CertificateSelector)state!).ReloadFromTimer(), this, ReloadInterval, ReloadInterval);
            return _current;
        }
    }

    /// <summary>The matching certificate of the PEM files or the configured store (no side effects).</summary>
    public X509Certificate2? Find()
    {
        var settings = _options.CurrentValue.Hosting.Certificate;
        if (IsPem(settings))
        {
            return FindPem(settings, _timeProvider.GetUtcNow()).Certificate;
        }

        var storeName = Enum.TryParse<StoreName>(settings.Store, ignoreCase: true, out var name) ? name : StoreName.My;
        var location = Enum.TryParse<StoreLocation>(settings.Location, ignoreCase: true, out var loc) ? loc : StoreLocation.LocalMachine;
        return Select(_store.GetCertificates(storeName, location), settings, _timeProvider.GetUtcNow());
    }

    /// <summary>The selection rule over <paramref name="certificates"/>.</summary>
    public static X509Certificate2? Select(IEnumerable<X509Certificate2> certificates, CertificateOptions settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(settings);
        var usable = certificates.Where(c => c.HasPrivateKey && IsValid(c, now));
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

    /// <summary>True when the certificate comes from PEM files (<see cref="CertificateOptions.Path"/> set).</summary>
    public static bool IsPem(CertificateOptions settings) => !string.IsNullOrWhiteSpace(settings?.Path);

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _fileChangeTimer?.Dispose();
            _fileChangeTimer = null;
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>The PEM pair when it is valid now and carries serverAuth; otherwise null with the reason (a pair problem of
    /// <see cref="PemCertificateSource"/>, or <c>expired</c> / <c>noServerAuth</c>).</summary>
    private static (X509Certificate2? Certificate, string? Problem) FindPem(CertificateOptions settings, DateTimeOffset now)
    {
        var loaded = PemCertificateSource.Load(settings.Path!, settings.KeyPath);
        if (loaded.Certificate is not { } certificate)
        {
            return (null, loaded.Problem);
        }

        if (!IsValid(certificate, now))
        {
            certificate.Dispose();
            return (null, "expired");
        }

        if (!HasServerAuth(certificate))
        {
            certificate.Dispose();
            return (null, "noServerAuth");
        }

        return (certificate, null);
    }

    /// <summary>Reloads shortly after a change of the PEM files (watcher of their folder, started once).</summary>
    private void WatchPemFiles(CertificateOptions settings)
    {
        if (_watcher is not null || Path.GetDirectoryName(Path.GetFullPath(settings.Path!)) is not { } folder || !System.IO.Directory.Exists(folder))
        {
            return;
        }

        try
        {
            _fileChangeTimer = _timeProvider.CreateTimer(static state => ((CertificateSelector)state!).ReloadFromTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += OnPemFileChanged;
            _watcher.Created += OnPemFileChanged;
            _watcher.Renamed += OnPemFileChanged;
            _watcher.Deleted += OnPemFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // No change notifications (e.g. inotify limit): the 10-minute reload still picks the renewal up.
            _watcher?.Dispose();
            _watcher = null;
            Log.WatchFailed(_logger, ex.GetType().Name);
        }
    }

    private void OnPemFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            _fileChangeTimer?.Change(FileChangeDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>A PEM pair that cannot be applied: Error <see cref="KeyHealthCode"/> while the current certificate (if any)
    /// keeps serving; logged once per problem. A missing or expired file is left to the start-up check (exit code 4) and
    /// the expiry warning.</summary>
    private void ReportPemProblem(string? problem)
    {
        if (problem is PemCertificateSource.KeyMismatch or PemCertificateSource.Invalid or PemCertificateSource.Unreadable)
        {
            _keyProblemReported = true;
            Report(KeyHealthCode, HealthState.Error, "The TLS key file does not belong to the certificate file or cannot be read; the current certificate stays in use.", problem);
            if (!string.Equals(_pemProblem, problem, StringComparison.Ordinal))
            {
                Log.PemPairUnusable(_logger, problem);
            }
        }
        else if (_pemProblem is not null && problem is null)
        {
            ClearKeyProblem();
        }

        _pemProblem = problem;
    }

    private static bool IsValid(X509Certificate2 certificate, DateTimeOffset now) =>
        certificate.NotBefore <= now.LocalDateTime && certificate.NotAfter > now.LocalDateTime;

    private void ReloadFromTimer()
    {
        try
        {
            Reload();
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            // The certificate store could not be read this time: keep the current certificate, try again next interval.
            Log.ReloadFailed(_logger, ex.GetType().Name);
        }
    }

    private void ReportKeyProblem(X509Certificate2 certificate, bool keepingPrevious)
    {
        _keyProblemReported = true;
        Report(
            KeyHealthCode,
            keepingPrevious ? HealthState.Warning : HealthState.Error,
            keepingPrevious
                ? "A newer TLS certificate was found, but the service account cannot use its private key; the previous certificate stays in use."
                : "The service account cannot use the private key of the TLS certificate; TLS handshakes fail.",
            certificate.Thumbprint);
        // Once per thumbprint AND state: the Warning while the previous certificate still serves, and again (Error) when
        // it has expired and TLS handshakes start failing - the moment the administrator must act.
        if (!_loggedKeyProblems.Add(certificate.Thumbprint + (keepingPrevious ? "|kept" : "|serving")))
        {
            return;
        }

        if (keepingPrevious)
        {
            Log.RenewedKeyUnusable(_logger, certificate.Thumbprint);
        }
        else
        {
            Log.KeyUnusable(_logger, certificate.Thumbprint);
        }
    }

    private void ClearKeyProblem()
    {
        if (_keyProblemReported)
        {
            _keyProblemReported = false;
            Report(KeyHealthCode, HealthState.Ok, "The service account can use the private key of the TLS certificate.");
        }
    }

    private void CheckExpiry(X509Certificate2? certificate, DateTimeOffset now)
    {
        if (certificate is null)
        {
            return;
        }

        var remaining = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero) - now;
        if (remaining < ExpiryWarning)
        {
            var days = Math.Max(0, (int)remaining.TotalDays).ToString(CultureInfo.InvariantCulture);
            _expiryReported = true;
            Report(ExpiryHealthCode, HealthState.Warning, "The TLS certificate expires within 30 days.", days);
            if (_loggedExpiry.Add(certificate.Thumbprint))
            {
                Log.CertificateExpiring(_logger, days);
            }
        }
        else if (_expiryReported)
        {
            _expiryReported = false;
            Report(ExpiryHealthCode, HealthState.Ok, "The TLS certificate is valid for more than 30 days.");
        }
    }

    private void Report(string code, HealthState state, string detail, params string[] args) =>
        _health?.Report(new HealthCheckDto { Code = code, State = state, Detail = detail, Args = args, CheckedAt = _timeProvider.GetUtcNow() });

    private static bool HasServerAuth(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(u => u.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ServerAuthOid));

    private static bool SubjectMatches(X509Certificate2 certificate, string subject) =>
        string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false), subject, StringComparison.OrdinalIgnoreCase)
        || string.Equals(certificate.Subject, subject, StringComparison.OrdinalIgnoreCase)
        || string.Equals(certificate.Subject, "CN=" + subject, StringComparison.OrdinalIgnoreCase);

    private static partial class Log
    {
        [LoggerMessage(EventId = EventLogIds.RuntimeOrCertificate, Level = LogLevel.Warning,
            Message = "A newer TLS certificate ({Thumbprint}) was found, but the service account cannot use its private key; grant it Read (certlm.msc → Manage Private Keys). The previous certificate stays in use.")]
        public static partial void RenewedKeyUnusable(ILogger logger, string thumbprint);

        [LoggerMessage(EventId = EventLogIds.RuntimeOrCertificate, Level = LogLevel.Error,
            Message = "The service account cannot use the private key of the TLS certificate {Thumbprint}; grant it Read (certlm.msc → Manage Private Keys).")]
        public static partial void KeyUnusable(ILogger logger, string thumbprint);

        [LoggerMessage(EventId = EventLogIds.RuntimeOrCertificate, Level = LogLevel.Warning, Message = "The TLS certificate expires in {Days} day(s).")]
        public static partial void CertificateExpiring(ILogger logger, string days);

        [LoggerMessage(EventId = EventLogIds.RuntimeOrCertificate, Level = LogLevel.Warning, Message = "The certificate store could not be read ({ErrorType}); the current TLS certificate stays in use.")]
        public static partial void ReloadFailed(ILogger logger, string errorType);

        [LoggerMessage(EventId = EventLogIds.RuntimeOrCertificate, Level = LogLevel.Error,
            Message = "The TLS certificate and key files cannot be used together ({Problem}); the current certificate stays in use. Install a matching pair (admng-wd.crt / admng-wd.key).")]
        public static partial void PemPairUnusable(ILogger logger, string problem);

        [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "The TLS certificate folder cannot be watched ({ErrorType}); renewals are picked up by the 10-minute reload.")]
        public static partial void WatchFailed(ILogger logger, string errorType);
    }
}
