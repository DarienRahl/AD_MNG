using System.Globalization;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Pki;

/// <summary>A row with a health state that the overview re-evaluates when shown (thresholds, clock – decision 2).</summary>
public abstract partial class PkiStatusRowViewModel : ObservableObject
{
    /// <summary>The state (the view colours it).</summary>
    [ObservableProperty]
    public partial PkiHealthStatus Status { get; internal set; }

    /// <summary>The state as text.</summary>
    [ObservableProperty]
    public partial string StatusText { get; internal set; } = string.Empty;

    /// <summary>The state needs attention (expiring, expired, unavailable).</summary>
    public bool IsProblem => Status is PkiHealthStatus.Expiring or PkiHealthStatus.Expired or PkiHealthStatus.Unavailable;

    partial void OnStatusChanged(PkiHealthStatus value) => OnPropertyChanged(nameof(IsProblem));
}

/// <summary>An enterprise CA of the overview (<i>Urzędy przedsiębiorstwa</i>).</summary>
public sealed partial class PkiCaRowViewModel : PkiStatusRowViewModel
{
    internal PkiCaRowViewModel(PkiEnrollmentService ca, CertificateSummary? current)
    {
        Ca = ca;
        Current = current;
    }

    /// <summary>The enterprise CA as read.</summary>
    public PkiEnrollmentService Ca { get; }

    /// <summary>Its newest parsed certificate, or null.</summary>
    public CertificateSummary? Current { get; }

    public string Dn => Ca.Dn;

    /// <summary>The CA name (displayName, else cn).</summary>
    public string Name => string.IsNullOrWhiteSpace(Ca.DisplayName) ? Ca.Name : Ca.DisplayName!;

    public string Host => Ca.HostName ?? string.Empty;

    public string ValidToText { get; internal init; } = string.Empty;

    public string NtAuthText { get; internal init; } = string.Empty;

    public string TemplatesText => Ca.PublishedTemplates.Count.ToString(CultureInfo.CurrentCulture);

    /// <summary>The CA host's computer object resolved in the window's domain.</summary>
    public bool CanOpenComputer => Ca.ComputerDn is not null;

    /// <summary>Why <see cref="CanOpenComputer"/> is false (another domain, not found), else empty.</summary>
    public string ComputerNote { get; internal init; } = string.Empty;

    public bool HasComputerNote => ComputerNote.Length > 0;

    /// <summary>The CA issues logon certificates but is not in NTAuth (README: <i>ostrzeżenie</i>), else empty.</summary>
    public string NtAuthWarning { get; internal init; } = string.Empty;

    public bool HasNtAuthWarning => NtAuthWarning.Length > 0;

    /// <summary>The certificate's fields (subject, issuer, validity with the days left, key, signature, serial, SHA-256, renewals).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PkiDetailLine> Details { get; internal set; } = [];

    /// <summary>The certification path read from AD (the CA first).</summary>
    public IReadOnlyList<string> Chain { get; internal init; } = [];

    /// <summary><i>Szablony certyfikatów do wystawienia</i> (cn values; <i>Otwórz szablon</i>).</summary>
    public IReadOnlyList<string> PublishedTemplates => Ca.PublishedTemplates;

    /// <summary>The CES URIs.</summary>
    public IReadOnlyList<string> EnrollmentServers => Ca.EnrollmentServers;

    public bool HasEnrollmentServers => Ca.EnrollmentServers.Count > 0;

    /// <summary>Kind and state only.</summary>
    public override string ToString() => $"{nameof(PkiCaRowViewModel)}({Status})";
}

/// <summary>A certificate of a trust store (<i>Zaufane urzędy główne i pośrednie</i>, NTAuth).</summary>
public sealed partial class PkiCertificateRowViewModel : PkiStatusRowViewModel
{
    internal PkiCertificateRowViewModel(PkiCaStore store, PkiCaObject owner, PkiCertificate certificate)
    {
        Store = store;
        Owner = owner;
        Certificate = certificate;
    }

    public PkiCaStore Store { get; }

    /// <summary>The <c>certificationAuthority</c> object it is stored on.</summary>
    public PkiCaObject Owner { get; }

    public PkiCertificate Certificate { get; }

    public CertificateSummary? Summary => Certificate.Summary;

    public string StoreText { get; internal init; } = string.Empty;

    public string Subject => Summary is { } s ? PkiDisplay.CommonName(s.Subject) : Owner.Name;

    public string Issuer => Summary is { } s ? PkiDisplay.CommonName(s.Issuer) : string.Empty;

    public string ValidToText { get; internal init; } = string.Empty;

    public string Thumbprint => Summary?.Thumbprint ?? string.Empty;

    /// <summary>The enterprise CA whose certificate this is (NTAuth match), or "—".</summary>
    public string EnterpriseCaText { get; internal init; } = string.Empty;

    /// <summary>The certificate's fields (the days left follow the clock).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PkiDetailLine> Details { get; internal set; } = [];

    /// <summary>Store and state only.</summary>
    public override string ToString() => $"{nameof(PkiCertificateRowViewModel)}({Store}, {Status})";
}

/// <summary>One CRL published in <c>CN=CDP</c> (base, delta or authority list).</summary>
public sealed partial class PkiCrlRowViewModel : PkiStatusRowViewModel
{
    internal PkiCrlRowViewModel(PkiCrlList list, PkiCrl crl, string kindText)
    {
        List = list;
        Crl = crl;
        KindText = kindText;
    }

    public PkiCrlList List { get; }

    public PkiCrl Crl { get; }

    public string Host => List.HostName ?? string.Empty;

    public string Name => List.Name;

    public string CaText { get; internal init; } = string.Empty;

    public string KindText { get; }

    public string NumberText { get; internal init; } = string.Empty;

    public string ThisUpdateText { get; internal init; } = string.Empty;

    public string NextUpdateText { get; internal init; } = string.Empty;

    public string RevokedText { get; internal init; } = string.Empty;

    /// <summary>State only.</summary>
    public override string ToString() => $"{nameof(PkiCrlRowViewModel)}({Status})";
}

/// <summary>A key recovery agent certificate (<c>CN=KRA</c>).</summary>
public sealed partial class PkiKraRowViewModel : PkiStatusRowViewModel
{
    internal PkiKraRowViewModel(PkiKraAgent agent, PkiCertificate certificate)
    {
        Agent = agent;
        Certificate = certificate;
    }

    public PkiKraAgent Agent { get; }

    public PkiCertificate Certificate { get; }

    public string Name => Agent.Name;

    public string Subject => Certificate.Summary is { } s ? PkiDisplay.CommonName(s.Subject) : string.Empty;

    public string Issuer => Certificate.Summary is { } s ? PkiDisplay.CommonName(s.Issuer) : string.Empty;

    public string ValidToText { get; internal init; } = string.Empty;

    /// <summary>State only.</summary>
    public override string ToString() => $"{nameof(PkiKraRowViewModel)}({Status})";
}

/// <summary>An OID object of <c>CN=OID</c> with its group link (ESC13).</summary>
public sealed class PkiOidRowViewModel
{
    internal PkiOidRowViewModel(PkiOidObject oid, string kindText)
    {
        Oid = oid;
        KindText = kindText;
    }

    public PkiOidObject Oid { get; }

    public string DisplayName => Oid.DisplayName ?? string.Empty;

    public string OidText => Oid.Oid;

    public string KindText { get; }

    /// <summary>The linked group's name (its RDN value), empty without a link.</summary>
    public string GroupLinkText => PkiDisplay.LeafName(Oid.GroupLinkDn);

    public bool HasGroupLink => Oid.GroupLinkDn is not null;

    /// <summary>Whether it links a group only.</summary>
    public override string ToString() => $"{nameof(PkiOidRowViewModel)}({(HasGroupLink ? "group link" : "no link")})";
}

/// <summary>One row of the <i>Zdrowie</i> tab (<c>PkiHealth.Evaluate</c>, re-evaluated when shown).</summary>
public sealed class PkiHealthRowViewModel
{
    internal PkiHealthRowViewModel(PkiHealthRow row, string caText, string itemText, string statusText, string validUntilText, string httpText)
    {
        Row = row;
        CaText = caText;
        ItemText = itemText;
        StatusText = statusText;
        ValidUntilText = validUntilText;
        HttpText = httpText;
    }

    public PkiHealthRow Row { get; }

    public PkiHealthStatus Status => Row.Status;

    public bool IsProblem => Status is PkiHealthStatus.Expiring or PkiHealthStatus.Expired or PkiHealthStatus.Unavailable;

    /// <summary>The enterprise CA (empty for KRA and NTAuth rows).</summary>
    public string CaText { get; }

    /// <summary>"Lokalizacja AIA #1".</summary>
    public string ItemText { get; }

    public string StatusText { get; }

    public string ValidUntilText { get; }

    public string Url => Row.Url ?? string.Empty;

    /// <summary>The HTTP check's outcome for this URL (empty before <i>Sprawdź adresy HTTP</i> or for other schemes).</summary>
    public string HttpText { get; }

    /// <summary>Item and state only.</summary>
    public override string ToString() => $"{nameof(PkiHealthRowViewModel)}({Row.Item}, {Status})";
}

/// <summary>One HTTP URL checked on the operator's button (AIA, CDP, delta CRL, OCSP).</summary>
/// <param name="Result">The check's result.</param>
/// <param name="KindText">What the URL is for.</param>
/// <param name="OutcomeText">The outcome (with the HTTP status code).</param>
public sealed record PkiHttpRowViewModel(PkiHttpCheckResult Result, string KindText, string OutcomeText)
{
    public string Url => Result.Url;

    public bool IsOk => Result.Outcome == PkiHttpOutcome.Ok;

    /// <summary>Kind and outcome only.</summary>
    public override string ToString() => $"{nameof(PkiHttpRowViewModel)}({Result.Kind}, {Result.Outcome})";
}

/// <summary>The verdict of one DC LDAPS / GC-LDAPS port (<i>Certyfikaty LDAPS</i>).</summary>
public sealed partial class PkiLdapsRowViewModel : ObservableObject
{
    internal PkiLdapsRowViewModel(string host, int port, DcTlsCheck? check)
    {
        Host = host;
        Port = port;
        Check = check;
    }

    public string Host { get; }

    public int Port { get; }

    /// <summary>The probe's result; null when the window has no probe (only the KDC column is filled).</summary>
    public DcTlsCheck? Check { get; }

    public string PortText { get; internal init; } = string.Empty;

    public string OutcomeText { get; internal init; } = string.Empty;

    /// <summary>The problems of the presented certificate ("OK" without any), re-evaluated with the thresholds.</summary>
    [ObservableProperty]
    public partial string ChecksText { get; internal set; } = string.Empty;

    /// <summary>The certificate has an error (wrong name, no server auth, expired, no AD root, issuer outside NTAuth) or the port gave none.</summary>
    [ObservableProperty]
    public partial bool IsProblem { get; internal set; }

    /// <summary>A warning only (expiring, the domain name missing from the SAN).</summary>
    [ObservableProperty]
    public partial bool IsWarning { get; internal set; }

    public string ValidToText { get; internal init; } = string.Empty;

    public string SubjectText { get; internal init; } = string.Empty;

    /// <summary>The chain status names of the build (tooltip).</summary>
    public string ChainStatusText { get; internal init; } = string.Empty;

    /// <summary>The KDC indicator of the DC from its published certificates (636 rows only).</summary>
    public string KdcText { get; internal init; } = string.Empty;

    /// <summary>Port and outcome only (never the host).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{nameof(PkiLdapsRowViewModel)}({Port}: {Check?.Outcome})");
}
