using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;

namespace AdMng.Presentation.Pki;

/// <summary>Which PKI document the shell should open (or select when it is open) – docs/dev/p-certificate-services.md 2.5.</summary>
public enum PkiDocumentKind
{
    /// <summary><i>Usługi kluczy publicznych</i>: the overview with the health and DC LDAPS tabs (W5).</summary>
    Overview,

    /// <summary><i>Szablony certyfikatów</i> (W5).</summary>
    Templates,

    /// <summary><i>Przegląd bezpieczeństwa AD CS</i> (W6).</summary>
    SecurityReview,

    /// <summary><i>Gotowość na silne mapowanie i NTAuth</i> (W6).</summary>
    Readiness,
}

/// <summary>A request to open a PKI document in the connection window's shell (one tab per kind and window).</summary>
/// <param name="Kind">The document.</param>
public sealed record PkiDocumentRequest(PkiDocumentKind Kind)
{
    /// <summary>The template the templates document selects (<i>Otwórz szablon</i>), its cn.</summary>
    public string? TemplateCn { get; init; }

    /// <summary>The enterprise CA the overview selects (<i>Otwórz urząd</i>), its DN.</summary>
    public string? CaDn { get; init; }

    /// <summary>The finding code the security review filters to.</summary>
    public string? FindingCode { get; init; }

    /// <summary>The kind only (never a template name or DN).</summary>
    public override string ToString() => $"{nameof(PkiDocumentRequest)}({Kind})";
}

/// <summary>
/// The per-window PKI cache (scoped; implementation W5, <c>PkiSnapshotService</c>): the snapshot, the enrollment
/// expansion, the domain controllers, the DC certificate checks and the HTTP results – keyed on the session instance and
/// its server, dropped on <c>SessionChanged</c> and <see cref="Invalidate"/> (F5). One build per item is shared by all
/// callers; a caller's token ends only that caller's wait. In memory only (rule 1).
/// </summary>
public interface IPkiSnapshotService
{
    /// <summary>The last snapshot of the current session, null before the first read.</summary>
    PkiSnapshot? Current { get; }

    /// <summary>When <see cref="Current"/> was read.</summary>
    DateTimeOffset? ReadAt { get; }

    /// <summary>A build runs.</summary>
    bool IsBuilding { get; }

    /// <summary>The last expansion of the current session, null before it was built.</summary>
    PkiEnrollmentExpansion? Expansion { get; }

    /// <summary>The last DC certificate checks, null before they were run (the readiness report shows them when present).</summary>
    IReadOnlyList<DcTlsCheck>? DcCertificates { get; }

    /// <summary>The results of the last <see cref="CheckHttpAsync"/>, null before the operator pressed <i>Sprawdź adresy HTTP</i>.</summary>
    IReadOnlyList<PkiHttpCheckResult>? HttpResults { get; }

    /// <summary>Raised (on the UI dispatcher) after an item was replaced or everything was dropped.</summary>
    event EventHandler? Changed;

    /// <summary>The snapshot; <paramref name="refresh"/> reads it again.</summary>
    Task<PkiSnapshot> GetAsync(bool refresh, IProgress<PkiReadProgress>? progress, CancellationToken cancellationToken);

    /// <summary>The enrollment expansion (reads the group graph once, then the one account pass).</summary>
    Task<PkiEnrollmentExpansion> GetExpansionAsync(bool refresh, IProgress<PkiExpansionProgress>? progress, CancellationToken cancellationToken);

    /// <summary>The window domain's DCs with their published certificates (LDAP only; the KDC indicator).</summary>
    Task<IReadOnlyList<PkiDomainController>> GetDomainControllersAsync(bool refresh, CancellationToken cancellationToken);

    /// <summary>The DC LDAPS / GC-LDAPS checks (TLS probes, at most 4 in parallel; only on explicit navigation or a button).</summary>
    Task<IReadOnlyList<DcTlsCheck>> GetDcCertificatesAsync(bool refresh, CancellationToken cancellationToken);

    /// <summary>Checks the HTTP URLs of the snapshot (the operator's button only) and keeps the results in <see cref="HttpResults"/>.</summary>
    Task<IReadOnlyList<PkiHttpCheckResult>> CheckHttpAsync(IProgress<int>? progress, CancellationToken cancellationToken);

    /// <summary>Cancels the builds and drops everything (F5, another DC, reconnect, disconnect).</summary>
    void Invalidate();
}
