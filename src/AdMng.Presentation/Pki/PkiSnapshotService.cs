using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Dialogs;

namespace AdMng.Presentation.Pki;

/// <summary>
/// The per-window PKI cache (docs/dev/p-certificate-services.md 2.5, W5; modelled on <c>GpoSettingsIndexService</c>):
/// caches, keyed on the session instance and its server, the snapshot, the enrollment expansion, the domain controllers,
/// the DC certificate checks (at most 4 probes in parallel, the current DC first, 3269 only for GCs) and the HTTP results.
/// One build per item shared by all callers, run on <c>Task.Run</c> under the service's own cancellation linked to the
/// window's lifetime; <c>SessionChanged</c> and <see cref="Invalidate"/> cancel and drop everything; <see cref="Changed"/>
/// fires after each replacement; <see cref="Dispose"/> unsubscribes and cancels. The contract stub stores its dependencies
/// and answers "nothing cached" so the window scope can create and dispose it; W5 writes the bodies.
/// </summary>
public sealed class PkiSnapshotService : IPkiSnapshotService, IDisposable
{
    /// <param name="ui">The window's UI services (session manager, dispatcher, clock, lifetime).</param>
    /// <param name="tlsProbe">The DC LDAPS probe (null: <see cref="GetDcCertificatesAsync"/> reports it unavailable).</param>
    /// <param name="healthProbe">The HTTP check (null: <see cref="CheckHttpAsync"/> reports it unavailable).</param>
    public PkiSnapshotService(DialogServices ui, IDomainControllerTlsProbe? tlsProbe, IPkiHealthProbe? healthProbe)
    {
        Ui = ui ?? throw new ArgumentNullException(nameof(ui));
        TlsProbe = tlsProbe;
        HealthProbe = healthProbe;
    }

    /// <summary>The window's UI services.</summary>
    public DialogServices Ui { get; }

    /// <summary>The DC LDAPS probe, or null.</summary>
    public IDomainControllerTlsProbe? TlsProbe { get; }

    /// <summary>The HTTP check, or null.</summary>
    public IPkiHealthProbe? HealthProbe { get; }

    public PkiSnapshot? Current => null;

    public DateTimeOffset? ReadAt => null;

    public bool IsBuilding => false;

    public PkiEnrollmentExpansion? Expansion => null;

    public IReadOnlyList<DcTlsCheck>? DcCertificates => null;

    public IReadOnlyList<PkiHttpCheckResult>? HttpResults => null;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public Task<PkiSnapshot> GetAsync(bool refresh, IProgress<PkiReadProgress>? progress, CancellationToken cancellationToken) =>
        throw new NotImplementedException("P1 W5: PkiSnapshotService.GetAsync");

    public Task<PkiEnrollmentExpansion> GetExpansionAsync(bool refresh, IProgress<PkiExpansionProgress>? progress, CancellationToken cancellationToken) =>
        throw new NotImplementedException("P1 W5: PkiSnapshotService.GetExpansionAsync");

    public Task<IReadOnlyList<PkiDomainController>> GetDomainControllersAsync(bool refresh, CancellationToken cancellationToken) =>
        throw new NotImplementedException("P1 W5: PkiSnapshotService.GetDomainControllersAsync");

    public Task<IReadOnlyList<DcTlsCheck>> GetDcCertificatesAsync(bool refresh, CancellationToken cancellationToken) =>
        throw new NotImplementedException("P1 W5: PkiSnapshotService.GetDcCertificatesAsync");

    public Task<IReadOnlyList<PkiHttpCheckResult>> CheckHttpAsync(IProgress<int>? progress, CancellationToken cancellationToken) =>
        throw new NotImplementedException("P1 W5: PkiSnapshotService.CheckHttpAsync");

    /// <summary>Nothing is cached yet in the contract stub (W5: cancel the builds, drop everything, raise <see cref="Changed"/>).</summary>
    public void Invalidate()
    {
    }

    /// <summary>Nothing to release in the contract stub (W5: unsubscribe <c>SessionChanged</c>, cancel).</summary>
    public void Dispose()
    {
    }
}
